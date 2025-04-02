using System.Buffers;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Pipelines;
using System.Numerics;
using System.Runtime;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

var help = """
 Program wykonuje przekształcenia bitmapy przez mnożenie każdego piksela (i sąsiadujących pikseli) przez podaną macierz.
 Wywołanie ../z2.exe [liczba_powtórzeń|--help] [macierz_przekształceń] [plik_wejściowy] [plik_wyjściowy]

 Parametry:
 - --help : Wyświetla instrukcję uruchomienia programu.
 - liczba_powtórzeń : Ile razy operacja przekształcania bitmapy ma być powtórzona np. 2. Domyślnie: 1
 - macierz_przekształceń : Macierz liczbowa w formacie tablicy tablic; np. [[1,2,3],[3,-15,2],[3,2,1]]. Program pyta interaktywnie gdy nie podano. Domyślnie: [[0,1,0],[1,-4,1],[0,1,0]]
 - plik_wejściowy : Plik przetwarzanej bitmapy. Program pyta interaktywnie gdy nie podano.
 - plik_wyjściowy : Plik docelowy przetworzonej bitmapy. Program nie generuje pliku gdy niepodano.
 
 Zmienne środowiskowe:
 - ztp_custom_collect=0 / ztp_custom_collect=1 / ztp_custom_collect=2 : Powoduje wywołanie wymuszonego czyszczenia pamięci po każdej przetworzonej linii pikseli. Wartość zmiennej oznacza poziom oczyszczanej sterty.
 - ztp_pooling=1 : Tymczasowa tablica kolorów przy przetwarzaniu będzie powtórnie wykorzystywana poprzez mechanizm poolingu. W przeciwnym razie przy każdym przetwarzaniu będzie alokowana nowa tablica.
 - ztp_pooling=0 lub brak : Przy każdym przetwarzaniu będzie alokowana nowa tablica tymczasowa.
 - ztp_unmanaged=1 : Przetwarzanie bitmapy będzie realizowane bezpośrednio na pamięci niezarządzalnej z wykorzystaniem tablicy Color'ów. 
 - ztp_unmanaged=2 : Przetwarzanie bitmapy będzie realizowane bezpośrednio na pamięci niezarządzalnej z wykorzystaniem tablicy bajtów. 
 - ztp_unmanaged=0 lub brak : Przetwarzanie będzie wykonane za pośrednictwem dostępnych metod Bitmap.
 - ztp_dispose=1 : Jeśli "1", to po każdym przetwarzaniu wywołana zostanie metoda "Dispose" na bitmapie. W przeciwnym razie metoda nie zostanie wywołana.
 - ztp_parallel_loop=1.. : Jeśli podana, przetwarzanie linii obrazu zostanie zrównoleglone metodą parallel loop. Wartość zmiennej to limit MaxDegreeOfParallelism.
 - ztp_tasks=1 : Jeśli "1", to przetwarzanie linii obrazu zostanie zrównoleglone przez zastosowanie obiektów Task.
 - ztp_simd=1 : Jeśli "1", to do operacji mnożenia i sumowania kolorów pikseli zostaną użyte obiekty ze wsparciem SIMD.
 """;

if(Environment.GetCommandLineArgs().ElementAtOrDefault(1) == "--help")
{
    Console.WriteLine(help);
    return;
}

string RequestMatrix()
{
    Console.WriteLine("Podaj macierz przekształceń (lub kliknij Enter aby wybrać domyślną):");
    var line = Console.ReadLine();
    if(line == "")
    {
        line = "[[0,1,0],[1,-4,1],[0,1,0]]";
        Console.WriteLine(line);
    }
    return line;
}

string RequestFile(string type)
{
    Console.WriteLine($"Podaj plik {type}:");
    return Console.ReadLine();
}

unsafe Color UnmanagedMemoryApplyMatrix(byte* bitmap, int[][] matrix, int xJump, int yJump)
{
    int r = 0, g = 0, b = 0;
    int halfLength = matrix.Length / 2;
    foreach (var i in Enumerable.Range(0, matrix.Length))
    {
        foreach (var j in Enumerable.Range(0, matrix.Length))
        {
            int offset = (xJump * (i - halfLength)) + (yJump * (j - halfLength));
            r += matrix[i][j] * *(bitmap + offset);
            g += matrix[i][j] * *(bitmap + offset + 1);
            b += matrix[i][j] * *(bitmap + offset + 2);
        }
    }
    return Color.FromArgb(Math.Clamp(r, 0, 255), Math.Clamp(g, 0, 255), Math.Clamp(b, 0, 255));
}

Color ManagedMemoryApplyMatrix(Bitmap bmp, int[][] matrix, int x, int y)
{
    int r = 0, g = 0, b = 0;
    foreach (var i in Enumerable.Range(x, matrix.Length))
    {
        foreach (var j in Enumerable.Range(y, matrix.Length))
        {
            var pixel = bmp.GetPixel(i, j);
            r += matrix[i - x][j - y] * pixel.R;
            g += matrix[i - x][j - y] * pixel.G;
            b += matrix[i - x][j - y] * pixel.B;
        }
    }
    return Color.FromArgb(Math.Clamp(r, 0, 255), Math.Clamp(g, 0, 255), Math.Clamp(b, 0, 255));
}

unsafe Color UnmanagedMemoryApplyMatrixSIMD(byte* bitmap, Vector3[] matrix, Vector3[] colors, int xJump, int yJump)
{
    float r = 0, g = 0, b = 0;
    foreach (var i in Enumerable.Range(0, 3))
    {
        foreach (var j in Enumerable.Range(0, 3))
        {
            int offset = (xJump * (i - 1)) + (yJump * (j - 1));
            colors[0][j] = *(bitmap + offset);
            colors[1][j] = *(bitmap + offset + 1);
            colors[2][j] = *(bitmap + offset + 2);
        }
        r += Vector3.Dot(colors[0], matrix[0]);
        g += Vector3.Dot(colors[1], matrix[1]);
        b += Vector3.Dot(colors[2], matrix[2]);
    }
    return Color.FromArgb((int)Math.Clamp(r, 0, 255), (int)Math.Clamp(g, 0, 255), (int)Math.Clamp(b, 0, 255));
}

void UnmanagedMemoryBitmapTransform(Bitmap bitmap, int[][] matrix)
{
    var customCollect = Environment.GetEnvironmentVariable("ztp_custom_collect");
    var pooling = Environment.GetEnvironmentVariable("ztp_pooling") == "1";

    unsafe
    {
        BitmapData bitmapData = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadWrite, bitmap.PixelFormat);

        int bytesPerPixel = Bitmap.GetPixelFormatSize(bitmap.PixelFormat) / 8;
        int heightInPixels = bitmapData.Height;
        int widthInBytes = bitmapData.Width * bytesPerPixel;
        var xRange = Enumerable.Range(matrix.Length / 2, bitmap.Width - matrix.Length);
        var yRange = Enumerable.Range(matrix.Length / 2, bitmap.Height - matrix.Length);
        byte* PtrFirstPixel = (byte*)bitmapData.Scan0;
        Color[] colors = pooling ? ArrayPool<Color>.Shared.Rent(bitmap.Width * bitmap.Height) : new Color[bitmap.Width * bitmap.Height];

        try
        {
            foreach (var y in yRange)
            {
                byte* currentLine = PtrFirstPixel + (y * bitmapData.Stride);
                foreach (var x in xRange)
                {
                    var color = UnmanagedMemoryApplyMatrix(currentLine + x * bytesPerPixel, matrix, bytesPerPixel, bitmapData.Stride);
                    colors[y * bitmap.Width + x] = color;
                }
                if (customCollect != null)
                {
                    GC.Collect(Int32.Parse(customCollect));
                }
            }
            foreach (var y in yRange)
            {
                byte* currentLine = PtrFirstPixel + (y * bitmapData.Stride);
                foreach (var x in xRange)
                {
                    var color = colors[y * bitmap.Width + x];
                    var xJump = x * bytesPerPixel;
                    currentLine[xJump] = color.R;
                    currentLine[xJump + 1] = color.G;
                    currentLine[xJump + 2] = color.B;
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
            if (pooling)
            {
                ArrayPool<Color>.Shared.Return(colors);
            }
        }

    }
}

void UnmanagedMemoryBitmapTransformSIMD(Bitmap bitmap, int[][] matrix)
{
    var customCollect = Environment.GetEnvironmentVariable("ztp_custom_collect");
    var pooling = Environment.GetEnvironmentVariable("ztp_pooling") == "1";
    Vector3[] simd = {
        new Vector3(matrix[0][0], matrix[0][1], matrix[0][2]),
        new Vector3(matrix[1][0], matrix[1][1], matrix[1][2]),
        new Vector3(matrix[2][0], matrix[2][1], matrix[2][2]),
    };
    Vector3[] simdTmp = { new Vector3(), new Vector3(), new Vector3() };

    unsafe
    {
        BitmapData bitmapData = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadWrite, bitmap.PixelFormat);

        int bytesPerPixel = Bitmap.GetPixelFormatSize(bitmap.PixelFormat) / 8;
        int heightInPixels = bitmapData.Height;
        int widthInBytes = bitmapData.Width * bytesPerPixel;
        var xRange = Enumerable.Range(matrix.Length / 2, bitmap.Width - matrix.Length);
        var yRange = Enumerable.Range(matrix.Length / 2, bitmap.Height - matrix.Length);
        byte* PtrFirstPixel = (byte*)bitmapData.Scan0;
        Color[] colors = pooling ? ArrayPool<Color>.Shared.Rent(bitmap.Width * bitmap.Height) : new Color[bitmap.Width * bitmap.Height];

        try
        {
            foreach (var y in yRange)
            {
                byte* currentLine = PtrFirstPixel + (y * bitmapData.Stride);
                foreach (var x in xRange)
                {
                    var color = UnmanagedMemoryApplyMatrixSIMD(currentLine + x * bytesPerPixel, simd, simdTmp, bytesPerPixel, bitmapData.Stride);
                    colors[y * bitmap.Width + x] = color;
                }
                if (customCollect != null)
                {
                    GC.Collect(Int32.Parse(customCollect));
                }
            }
            foreach (var y in yRange)
            {
                byte* currentLine = PtrFirstPixel + (y * bitmapData.Stride);
                foreach (var x in xRange)
                {
                    var color = colors[y * bitmap.Width + x];
                    var xJump = x * bytesPerPixel;
                    currentLine[xJump] = color.R;
                    currentLine[xJump + 1] = color.G;
                    currentLine[xJump + 2] = color.B;
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
            if (pooling)
            {
                ArrayPool<Color>.Shared.Return(colors);
            }
        }

    }
}

void UnmanagedMemoryBitmapTransformBytes(Bitmap bitmap, int[][] matrix)
{
    var customCollect = Environment.GetEnvironmentVariable("ztp_custom_collect");
    var pooling = Environment.GetEnvironmentVariable("ztp_pooling") == "1";

    unsafe
    {
        BitmapData bitmapData = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadWrite, bitmap.PixelFormat);

        int bytesPerPixel = Bitmap.GetPixelFormatSize(bitmap.PixelFormat) / 8;
        int heightInPixels = bitmapData.Height;
        int widthInBytes = bitmapData.Width * bytesPerPixel;
        var xRange = Enumerable.Range(matrix.Length / 2, bitmap.Width - matrix.Length);
        var yRange = Enumerable.Range(matrix.Length / 2, bitmap.Height - matrix.Length);
        byte* PtrFirstPixel = (byte*)bitmapData.Scan0;
        byte[] bytes = ArrayPool<byte>.Shared.Rent(widthInBytes * bitmap.Height);

        try
        {
            foreach (var y in yRange)
            {
                byte* currentLine = PtrFirstPixel + (y * bitmapData.Stride);
                foreach (var x in xRange)
                {
                    var color = UnmanagedMemoryApplyMatrix(currentLine + x * bytesPerPixel, matrix, bytesPerPixel, bitmapData.Stride);
                    bytes[y * widthInBytes + x * bytesPerPixel] = color.R;
                    bytes[y * widthInBytes + x * bytesPerPixel + 1] = color.G;
                    bytes[y * widthInBytes + x * bytesPerPixel + 2] = color.B;
                }
                if (customCollect != null)
                {
                    GC.Collect(Int32.Parse(customCollect));
                }
            }
            foreach (var y in yRange)
            {
                byte* currentLine = PtrFirstPixel + (y * bitmapData.Stride);
                foreach (var x in xRange)
                {
                    var xJump = x * bytesPerPixel;
                    currentLine[xJump] = bytes[y * widthInBytes + x * bytesPerPixel];
                    currentLine[xJump + 1] = bytes[y * widthInBytes + x * bytesPerPixel + 1];
                    currentLine[xJump + 2] = bytes[y * widthInBytes + x * bytesPerPixel + 1];
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
            if (pooling)
            {
                ArrayPool<byte>.Shared.Return(bytes);
            }
        }

    }
}

void ManagedMemoryBitmapTransform(Bitmap bitmap, int[][] matrix)
{
    var customCollect = Environment.GetEnvironmentVariable("ztp_custom_collect");
    var pooling = Environment.GetEnvironmentVariable("ztp_pooling") == "1";

    var xRange = Enumerable.Range(matrix.Length / 2, bitmap.Width - matrix.Length);
    var yRange = Enumerable.Range(matrix.Length / 2, bitmap.Height - matrix.Length);
    Color[] colors = pooling ? ArrayPool<Color>.Shared.Rent(bitmap.Width * bitmap.Height) : new Color[bitmap.Width * bitmap.Height];
    try
    {
        foreach (var y in yRange)
        {
            foreach (var x in xRange)
            {
                var color = ManagedMemoryApplyMatrix(bitmap, matrix, x, y);
                colors[y * bitmap.Width + x] = color;
            }
            if(customCollect != null)
            {
                GC.Collect(Int32.Parse(customCollect));
            }
        }
        foreach (var y in yRange)
        {
            foreach (var x in xRange)
            {
                bitmap.SetPixel(x, y, colors[y * bitmap.Width + x]);
            }
        }
    }
    finally
    {
        if (pooling)
        {
            ArrayPool<Color>.Shared.Return(colors);
        }
    }
}

#region Parallel_Loop
void UnmanagedMemoryBitmapTransformWithParallelLoop(Bitmap bitmap, int[][] matrix)
{
    var customCollect = Environment.GetEnvironmentVariable("ztp_custom_collect");
    var pooling = Environment.GetEnvironmentVariable("ztp_pooling") == "1";
    var parallelLoop = Environment.GetEnvironmentVariable("ztp_parallel_loop");
    var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = Int32.Parse(parallelLoop) };

    unsafe
    {
        BitmapData bitmapData = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadWrite, bitmap.PixelFormat);

        int bytesPerPixel = Bitmap.GetPixelFormatSize(bitmap.PixelFormat) / 8;
        int heightInPixels = bitmapData.Height;
        int widthInBytes = bitmapData.Width * bytesPerPixel;
        var xRange = Enumerable.Range(matrix.Length / 2, bitmap.Width - matrix.Length);
        var yRange = Enumerable.Range(matrix.Length / 2, bitmap.Height - matrix.Length);
        byte* PtrFirstPixel = (byte*)bitmapData.Scan0;
        byte[] bytes = ArrayPool<byte>.Shared.Rent(widthInBytes * bitmap.Height);

        try
        {
            Parallel.For(matrix.Length / 2, bitmap.Height - matrix.Length, parallelOptions, y =>
            {
                byte* currentLine = PtrFirstPixel + (y * bitmapData.Stride);
                foreach (var x in xRange)
                {
                    var color = UnmanagedMemoryApplyMatrix(currentLine + x * bytesPerPixel, matrix, bytesPerPixel, bitmapData.Stride);
                    bytes[y * widthInBytes + x * bytesPerPixel] = color.R;
                    bytes[y * widthInBytes + x * bytesPerPixel + 1] = color.G;
                    bytes[y * widthInBytes + x * bytesPerPixel + 2] = color.B;
                }
                if (customCollect != null)
                {
                    GC.Collect(Int32.Parse(customCollect));
                }
            });
            Parallel.For(matrix.Length / 2, bitmap.Height - matrix.Length, parallelOptions, y =>
            {
                byte* currentLine = PtrFirstPixel + (y * bitmapData.Stride);
                foreach (var x in xRange)
                {
                    var xJump = x * bytesPerPixel;
                    currentLine[xJump] = bytes[y * widthInBytes + x * bytesPerPixel];
                    currentLine[xJump + 1] = bytes[y * widthInBytes + x * bytesPerPixel + 1];
                    currentLine[xJump + 2] = bytes[y * widthInBytes + x * bytesPerPixel + 1];
                }
            });
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
            if (pooling)
            {
                ArrayPool<byte>.Shared.Return(bytes);
            }
        }

    }
}
#endregion

void UnmanagedMemoryBitmapTransformWithTasks(Bitmap bitmap, int[][] matrix)
{
    var customCollect = Environment.GetEnvironmentVariable("ztp_custom_collect");
    var pooling = Environment.GetEnvironmentVariable("ztp_pooling") == "1";

    unsafe
    {
        BitmapData bitmapData = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadWrite, bitmap.PixelFormat);

        int bytesPerPixel = Bitmap.GetPixelFormatSize(bitmap.PixelFormat) / 8;
        int heightInPixels = bitmapData.Height;
        int widthInBytes = bitmapData.Width * bytesPerPixel;
        var xRange = Enumerable.Range(matrix.Length / 2, bitmap.Width - matrix.Length);
        var yRange = Enumerable.Range(matrix.Length / 2, bitmap.Height - matrix.Length);
        byte* PtrFirstPixel = (byte*)bitmapData.Scan0;
        byte[] bytes = ArrayPool<byte>.Shared.Rent(widthInBytes * bitmap.Height);

        try
        {
            Task[] taskArray = new Task[bitmap.Height - matrix.Length];
            foreach (var y in yRange)
            {
                byte* currentLine = PtrFirstPixel + (y * bitmapData.Stride);
                taskArray[y - matrix.Length / 2] = Task.Factory.StartNew(() => {
                    foreach (var x in xRange)
                    {
                        var color = UnmanagedMemoryApplyMatrix(currentLine + x * bytesPerPixel, matrix, bytesPerPixel, bitmapData.Stride);
                        bytes[y * widthInBytes + x * bytesPerPixel] = color.R;
                        bytes[y * widthInBytes + x * bytesPerPixel + 1] = color.G;
                        bytes[y * widthInBytes + x * bytesPerPixel + 2] = color.B;
                    }
                    if (customCollect != null)
                    {
                        GC.Collect(Int32.Parse(customCollect));
                    }
                });
            }
            Task.WaitAll(taskArray);
            foreach (var y in yRange)
            {
                byte* currentLine = PtrFirstPixel + (y * bitmapData.Stride);
                taskArray[y - matrix.Length / 2] = Task.Factory.StartNew(() => {
                    foreach (var x in xRange)
                    {
                        var xJump = x * bytesPerPixel;
                        currentLine[xJump] = bytes[y * widthInBytes + x * bytesPerPixel];
                        currentLine[xJump + 1] = bytes[y * widthInBytes + x * bytesPerPixel + 1];
                        currentLine[xJump + 2] = bytes[y * widthInBytes + x * bytesPerPixel + 1];
                    }
                });
            }
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
            if (pooling)
            {
                ArrayPool<byte>.Shared.Return(bytes);
            }
        }

    }
}



int repeat = Int32.Parse(Environment.GetCommandLineArgs().ElementAtOrDefault(1) ?? "1");
var matrixInput = Environment.GetCommandLineArgs().ElementAtOrDefault(2) ?? RequestMatrix();
string input = Environment.GetCommandLineArgs().ElementAtOrDefault(3) ?? RequestFile("wejściowy");
string output = Environment.GetCommandLineArgs().ElementAtOrDefault(4) ?? "";

var unmanaged = Environment.GetEnvironmentVariable("ztp_unmanaged");
var parallelLoop = Environment.GetEnvironmentVariable("ztp_parallel_loop");
var tasks = Environment.GetEnvironmentVariable("ztp_tasks");
var simd = Environment.GetEnvironmentVariable("ztp_simd");
var dispose = Environment.GetEnvironmentVariable("ztp_dispose") == "1";

int[][] matrix = Regex
.Matches(matrixInput.Substring(1), @"\[([^]]+)]")
.Select(match => match.Groups[1].Value
  .Split(",", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
  .Select(str => Int32.Parse(str))
  .ToArray()
)
.ToArray();

foreach(var i in Enumerable.Range(1, repeat))
{
    var bitmap = new Bitmap(input);
    switch(unmanaged)
    {
        case "1":
            UnmanagedMemoryBitmapTransform(bitmap, matrix);
            break;
        case "2":
            UnmanagedMemoryBitmapTransformBytes(bitmap, matrix);
            break;
        default:
            if(parallelLoop != null)
            {
                UnmanagedMemoryBitmapTransformWithParallelLoop(bitmap, matrix);
                break;
            } else if(tasks != null) 
            {
                UnmanagedMemoryBitmapTransformWithTasks(bitmap, matrix);
            }
            else if (simd != null)
            {
                UnmanagedMemoryBitmapTransformSIMD(bitmap, matrix);
            }
            else
            {
                ManagedMemoryBitmapTransform(bitmap, matrix);
            }
            break;
    }
        
    if (output != "" && i == repeat)
    {
        Console.WriteLine(output);
        bitmap.Save(output);
    }
    if (dispose)
    {
        bitmap.Dispose();
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2);
    }
}
