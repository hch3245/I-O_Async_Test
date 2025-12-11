using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FileReadDemo
{
    internal static class Program
    {
        // I/O 후 인위적 지연 (CPU/IO 버전 공정 비교용)
        public const int delayMilliseconds = 0;

        static async Task Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            string folder = PrepareFilesFolder();

            while (true)
            {
                Console.Clear();
                Console.CursorVisible = true;

                Console.WriteLine("파일 읽기 데모 (공정 비교 + 스레드 모니터링 + FileStream 미리 열기 옵션)");
                Console.WriteLine("----------------------------------------------------------------");
                Console.WriteLine("1: 동기 버전");
                Console.WriteLine("2: CPU 비동기(Task.Run) 버전");
                Console.WriteLine("3: I/O 비동기(async/await) 버전");
                Console.WriteLine("OUT: 종료");
                Console.WriteLine();
                Console.Write("실행할 버전을 선택하세요 (1/2/3/OUT): ");

                string? input = Console.ReadLine();
                if (input is null)
                    continue;

                input = input.Trim().ToUpperInvariant();

                if (input == "OUT")
                {
                    break; // 프로그램 종료
                }

                char choice = (input.Length > 0 ? input[0] : '3');
                if (choice != '1' && choice != '2' && choice != '3')
                {
                    choice = '3'; // 기본값: I/O 비동기
                }

                // *** 여기서 FileStream 미리 열지 여부 선택 ***
                Console.Write("FileStream을 미리 열까요? (Y/N): ");
                string? preloadInput = Console.ReadLine();
                bool preloadStreams =
                    preloadInput is not null &&
                    preloadInput.Trim().Equals("Y", StringComparison.OrdinalIgnoreCase);

                // 공통 파일 목록
                var filePaths = Directory.GetFiles(folder)
                                         .OrderBy(f => f)
                                         .ToArray();

                // FileItem 배열 생성 (미리 열기 옵션에 따라 PreOpenedStream 채움)
                FileItem[] items = preloadStreams
                    ? CreatePreOpenedFileItems(filePaths)
                    : CreateLazyFileItems(filePaths);

                Console.Clear();
                Console.CursorVisible = false;

                using var cts = new CancellationTokenSource();

                // 애니메이션 시작
                var animator = new DotAnimator();
                var animTask = animator.RunAsync(cts.Token);

                // 스레드 모니터 시작
                ThreadPoolMonitor.Start();

                IFileReader reader = choice switch
                {
                    '1' => new SyncFileReader(items),
                    '2' => new CpuAsyncFileReader(items),
                    _ => new IoAsyncFileReader(items)
                };

                Ui.WriteStatus($"파일을 읽는 중입니다... (미리열기: {preloadStreams})");

                var sw = Stopwatch.StartNew();

                try
                {
                    await reader.RunAsync(cts.Token);
                }
                finally
                {
                    sw.Stop();

                    // 스레드 모니터 종료 & 통계 정보 가져오기
                    var stats = await ThreadPoolMonitor.StopAsync();

                    // 애니메이션 중지
                    cts.Cancel();
                    try
                    {
                        await animTask;
                    }
                    catch (TaskCanceledException)
                    {
                        // 무시
                    }

                    Console.CursorVisible = true;

                    // 하단에 요약 정보 출력
                    Ui.WriteBottom($"소요 시간: {sw.Elapsed} (미리열기: {preloadStreams})");

                    // 그 위 줄에 스레드 사용 최대/평균 표시
                    int infoLine = Math.Max(0, Console.WindowHeight - 4);
                    Console.SetCursorPosition(2, infoLine);

                    string statsText =
                        $"ThreadPool Busy Max/Avg - " +
                        $"Worker: {stats.MaxWorkerBusy}/{stats.AvgWorkerBusy:F1}, " +
                        $"IOCP: {stats.MaxIoBusy}/{stats.AvgIoBusy:F1}  " +
                        $"OS Threads Max/Avg: {stats.MaxOsThreads}/{stats.AvgOsThreads:F1}";

                    Console.Write(statsText.PadRight(Math.Max(0, Console.WindowWidth - 2)));

                    // 미리 연 파일 스트림 정리
                    if (preloadStreams)
                    {
                        foreach (var item in items)
                        {
                            item.PreOpenedStream?.Dispose();
                        }
                    }
                }

                Console.SetCursorPosition(0, Console.WindowHeight - 1);
                Console.Write("계속하려면 Enter, 종료하려면 OUT 입력 후 Enter: ");
                string? next = Console.ReadLine();
                if (next is not null &&
                    next.Trim().ToUpperInvariant() == "OUT")
                {
                    break;
                }
            }
        }

        private static FileItem[] CreateLazyFileItems(string[] filePaths)
        {
            // FileStream을 미리 열지 않고, 나중에 각 Reader가 new FileStream 하는 버전
            var list = new List<FileItem>(filePaths.Length);
            foreach (var path in filePaths)
            {
                list.Add(new FileItem(path, null));
            }
            return list.ToArray();
        }

        private static FileItem[] CreatePreOpenedFileItems(string[] filePaths)
        {
            // FileStream을 여기서 다 열어두고, Reader는 그대로 사용만 하는 버전
            // (주의: 파일 개수가 매우 많으면 핸들 수가 모자랄 수 있음)
            var list = new List<FileItem>(filePaths.Length);
            foreach (var path in filePaths)
            {
                var fs = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    FileReadConfig.BufferSize,
                    // 비동기/동기 비교를 위해 모두 Asynchronous 핸들로 연다.
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                list.Add(new FileItem(path, fs));
            }
            return list.ToArray();
        }

        private static string PrepareFilesFolder()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string folder = Path.Combine(baseDir, "Files");
            Directory.CreateDirectory(folder);

            // 필요시 값 조절
            const int fileSizeMB = 50;
            const int fileCount = 100;

            if (!Directory.EnumerateFiles(folder).Any())
            {
                byte[] buffer = new byte[1024 * 1024]; // 1MB 버퍼
                var rng = new Random();

                for (int i = 1; i <= fileCount; i++)
                {
                    string path = Path.Combine(folder, $"File{i:D3}.bin");

                    using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        for (int mb = 0; mb < fileSizeMB; mb++)
                        {
                            rng.NextBytes(buffer); // 1MB 랜덤 데이터
                            fs.Write(buffer, 0, buffer.Length);
                        }
                    }
                }
            }

            return folder;
        }
    }

    // =========================
    // FileItem: 경로 + (선택적) 미리 열린 스트림
    // =========================
    internal sealed class FileItem
    {
        public string Path { get; }
        public FileStream? PreOpenedStream { get; }

        public FileItem(string path, FileStream? preOpenedStream)
        {
            Path = path;
            PreOpenedStream = preOpenedStream;
        }
    }

    // =========================
    // 콘솔 레이아웃/동기화 헬퍼
    // =========================
    internal static class Ui
    {
        private static readonly object _syncRoot = new object();
        public static object SyncRoot => _syncRoot;

        // 상단 상태 라인
        public static void WriteStatus(string text)
        {
            lock (_syncRoot)
            {
                int left = 2;
                int top = 1;
                if (Console.WindowWidth <= left) return;

                Console.SetCursorPosition(left, top);
                string padded = text.PadRight(Math.Max(0, Console.WindowWidth - left));
                Console.Write(padded);
            }
        }

        // 실시간 스레드 정보 라인 (상태 라인 바로 아래)
        public static void WriteThreadStats(
            int workerBusy,
            int ioBusy,
            int osThreads,
            int maxWorker,
            int maxIo,
            int maxOs)
        {
            lock (_syncRoot)
            {
                int left = 2;
                int top = 2; // 상태 라인 바로 아래 줄
                if (Console.WindowWidth <= left) return;

                string text =
                    $"[실시간 스레드] Worker Busy: {workerBusy}, IOCP Busy: {ioBusy}, OS Threads: {osThreads}  " +
                    $"(Max W:{maxWorker}, IO:{maxIo}, OS:{maxOs})";

                Console.SetCursorPosition(left, top);
                string padded = text.PadRight(Math.Max(0, Console.WindowWidth - left));
                Console.Write(padded);
            }
        }

        // 하단 완료 메시지
        public static void WriteBottom(string text)
        {
            lock (_syncRoot)
            {
                int left = 2;
                int top = Math.Max(0, Console.WindowHeight - 3);
                if (Console.WindowWidth <= left) return;

                Console.SetCursorPosition(left, top);
                string padded = text.PadRight(Math.Max(0, Console.WindowWidth - left));
                Console.Write(padded);
            }
        }
    }

    // =========================
    // ThreadPool 모니터
    // =========================
    internal readonly struct ThreadPoolStats
    {
        public int MaxWorkerBusy { get; }
        public int MaxIoBusy { get; }
        public int MaxOsThreads { get; }

        public double AvgWorkerBusy { get; }
        public double AvgIoBusy { get; }
        public double AvgOsThreads { get; }

        public ThreadPoolStats(
            int maxWorkerBusy,
            int maxIoBusy,
            int maxOsThreads,
            double avgWorkerBusy,
            double avgIoBusy,
            double avgOsThreads)
        {
            MaxWorkerBusy = maxWorkerBusy;
            MaxIoBusy = maxIoBusy;
            MaxOsThreads = maxOsThreads;

            AvgWorkerBusy = avgWorkerBusy;
            AvgIoBusy = avgIoBusy;
            AvgOsThreads = avgOsThreads;
        }
    }

    internal static class ThreadPoolMonitor
    {
        private static CancellationTokenSource? _cts;
        private static Task? _monitorTask;

        private static int _maxWorkerBusy;
        private static int _maxIoBusy;
        private static int _maxOsThreads;

        // 평균 계산용 누적값들
        private static long _sumWorkerBusy;
        private static long _sumIoBusy;
        private static long _sumOsThreads;
        private static long _sampleCount;

        private static readonly object _lock = new object();

        public static void Start()
        {
            lock (_lock)
            {
                // 중복 시작 방지
                if (_monitorTask != null && !_monitorTask.IsCompleted)
                    return;

                _maxWorkerBusy = 0;
                _maxIoBusy = 0;
                _maxOsThreads = 0;

                _sumWorkerBusy = 0;
                _sumIoBusy = 0;
                _sumOsThreads = 0;
                _sampleCount = 0;

                _cts = new CancellationTokenSource();
                _monitorTask = Task.Run(() => MonitorLoopAsync(_cts.Token));
            }
        }

        public static async Task<ThreadPoolStats> StopAsync()
        {
            CancellationTokenSource? ctsLocal;
            Task? monitorLocal;

            lock (_lock)
            {
                ctsLocal = _cts;
                monitorLocal = _monitorTask;
                _cts = null;
                _monitorTask = null;
            }

            if (ctsLocal != null)
            {
                ctsLocal.Cancel();
            }

            if (monitorLocal != null)
            {
                try
                {
                    await monitorLocal.ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    // 무시
                }
            }

            // 평균 계산
            long sampleCount = Interlocked.Read(ref _sampleCount);
            double avgWorker = 0;
            double avgIo = 0;
            double avgOs = 0;

            if (sampleCount > 0)
            {
                avgWorker = (double)Interlocked.Read(ref _sumWorkerBusy) / sampleCount;
                avgIo = (double)Interlocked.Read(ref _sumIoBusy) / sampleCount;
                avgOs = (double)Interlocked.Read(ref _sumOsThreads) / sampleCount;
            }

            return new ThreadPoolStats(
                _maxWorkerBusy,
                _maxIoBusy,
                _maxOsThreads,
                avgWorker,
                avgIo,
                avgOs);
        }

        private static async Task MonitorLoopAsync(CancellationToken token)
        {
            // ThreadPool 최대값은 루프 밖에서 한 번만 받아도 됨
            ThreadPool.GetMaxThreads(out int maxWorker, out int maxIo);

            while (!token.IsCancellationRequested)
            {
                try
                {
                    ThreadPool.GetAvailableThreads(out int availWorker, out int availIo);
                    int busyWorker = maxWorker - availWorker;
                    int busyIo = maxIo - availIo;

                    int osThreads = 0;
                    try
                    {
                        using var proc = Process.GetCurrentProcess();
                        osThreads = proc.Threads.Count;
                    }
                    catch
                    {
                        // 일부 환경에서 Process 정보 가져오기 실패할 수 있음 -> 0으로 둠
                    }

                    // 최대값 갱신
                    if (busyWorker > _maxWorkerBusy) _maxWorkerBusy = busyWorker;
                    if (busyIo > _maxIoBusy) _maxIoBusy = busyIo;
                    if (osThreads > _maxOsThreads) _maxOsThreads = osThreads;

                    // 평균 계산용 누적
                    Interlocked.Add(ref _sumWorkerBusy, busyWorker);
                    Interlocked.Add(ref _sumIoBusy, busyIo);
                    Interlocked.Add(ref _sumOsThreads, osThreads);
                    Interlocked.Increment(ref _sampleCount);

                    // 화면에 실시간 표시
                    Ui.WriteThreadStats(
                        busyWorker,
                        busyIo,
                        osThreads,
                        _maxWorkerBusy,
                        _maxIoBusy,
                        _maxOsThreads);

                    await Task.Delay(200, token).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch
                {
                    // 모니터링 중 예외는 무시하고 계속
                }
            }
        }
    }

    // =========================
    // 도트 애니메이션
    // =========================
    internal sealed class DotAnimator
    {
        private readonly string[][] _frames = new[]
        {
            new[]
            {
                "   .   ",
                "  ...  ",
                " ..... ",
                "  ...  ",
                "   .   "
            },
            new[]
            {
                "       ",
                "   .   ",
                "  ...  ",
                "   .   ",
                "       "
            },
            new[]
            {
                " .     ",
                " ..    ",
                " ...   ",
                " ..    ",
                " .     "
            },
            new[]
            {
                "       ",
                " . .   ",
                "  .    ",
                " . .   ",
                "       "
            }
        };

        public async Task RunAsync(CancellationToken token)
        {
            int frameIndex = 0;

            while (!token.IsCancellationRequested)
            {
                DrawFrame(_frames[frameIndex]);
                frameIndex = (frameIndex + 1) % _frames.Length;

                try
                {
                    await Task.Delay(120, token);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }

        private void DrawFrame(string[] frame)
        {
            lock (Ui.SyncRoot)
            {
                int height = frame.Length;
                int width = frame[0].Length;

                int left = 0;
                int top = 0;

                try
                {
                    left = Math.Max(0, (Console.WindowWidth - width) / 2);
                    top = Math.Max(0, (Console.WindowHeight - height) / 2);
                }
                catch
                {
                    // 콘솔 크기 정보 못 가져오는 환경이면 그냥 (0,0)에 그림
                }

                for (int i = 0; i < height; i++)
                {
                    if (top + i >= Console.WindowHeight) break;

                    Console.SetCursorPosition(left, top + i);
                    string line = frame[i];
                    if (line.Length < width)
                        line = line.PadRight(width);

                    Console.Write(line);
                }
            }
        }
    }

    // =========================
    // 공통 인터페이스 / 설정
    // =========================
    internal interface IFileReader
    {
        Task RunAsync(CancellationToken token);
    }

    internal static class FileReadConfig
    {
        public const int BufferSize = 81920;

        public static readonly int MaxConcurrency =
            Math.Max(1, Environment.ProcessorCount);
    }

    // =========================
    // 1. 동기 버전
    // =========================
    internal sealed class SyncFileReader : IFileReader
    {
        private readonly FileItem[] _items;

        public SyncFileReader(FileItem[] items)
        {
            _items = items;
        }

        public Task RunAsync(CancellationToken token)
        {
            foreach (var item in _items)
            {
                if (token.IsCancellationRequested)
                    break;

                var fs = item.PreOpenedStream ??
                         new FileStream(
                             item.Path,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             FileReadConfig.BufferSize,
                             FileOptions.SequentialScan);

                try
                {
                    fs.CopyTo(Stream.Null, FileReadConfig.BufferSize);

                    // 인위적 지연
                    Thread.Sleep(Program.delayMilliseconds);

                    Ui.WriteStatus($"{Path.GetFileName(item.Path)} 를 읽어왔습니다. (동기)");
                }
                finally
                {
                    if (item.PreOpenedStream is null)
                    {
                        fs.Dispose();
                    }
                    // PreOpenedStream은 Program에서 일괄 Dispose
                }
            }

            Ui.WriteBottom("모든 파일을 읽어왔습니다. (동기 버전)");
            return Task.CompletedTask;
        }
    }

    // =========================
    // 2. CPU 비동기(Task.Run) 버전
    // =========================
    internal sealed class CpuAsyncFileReader : IFileReader
    {
        private readonly FileItem[] _items;

        public CpuAsyncFileReader(FileItem[] items)
        {
            _items = items;
        }

        public async Task RunAsync(CancellationToken token)
        {
            var tasks = new List<Task>();

            foreach (var item in _items)
            {
                if (token.IsCancellationRequested)
                    break;

                tasks.Add(Task.Run(() =>
                {
                    if (token.IsCancellationRequested)
                        return;

                    var fs = item.PreOpenedStream ??
                             new FileStream(
                                 item.Path,
                                 FileMode.Open,
                                 FileAccess.Read,
                                 FileShare.Read,
                                 FileReadConfig.BufferSize,
                                 FileOptions.SequentialScan);

                    try
                    {
                        fs.CopyTo(Stream.Null, FileReadConfig.BufferSize);
                        Thread.Sleep(Program.delayMilliseconds);

                        Ui.WriteStatus($"{Path.GetFileName(item.Path)} 를 읽어왔습니다. (CPU 비동기)");
                    }
                    finally
                    {
                        if (item.PreOpenedStream is null)
                        {
                            fs.Dispose();
                        }
                    }
                }, token));
            }

            await Task.WhenAll(tasks);

            Ui.WriteBottom("모든 파일을 읽어왔습니다. (CPU 비동기 / Task.Run 버전)");
        }
    }

    // =========================
    // 3. I/O 비동기 버전
    // =========================
    internal sealed class IoAsyncFileReader : IFileReader
    {
        private readonly FileItem[] _items;

        public IoAsyncFileReader(FileItem[] items)
        {
            _items = items;
        }

        public async Task RunAsync(CancellationToken token)
        {
            var tasks = new List<Task>();

            foreach (var item in _items)
            {
                if (token.IsCancellationRequested)
                    break;

                tasks.Add(ReadOneFileAsync(item, token));
            }

            await Task.WhenAll(tasks);

            Ui.WriteBottom("모든 파일을 읽어왔습니다. (I/O 비동기 버전)");
        }

        private async Task ReadOneFileAsync(FileItem item, CancellationToken token)
        {
            try
            {
                token.ThrowIfCancellationRequested();

                // 미리 열린 스트림이 있으면 그걸 쓰고,
                // 없으면 여기서 Asynchronous 핸들로 생성
                var fs = item.PreOpenedStream ??
                         new FileStream(
                             item.Path,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             FileReadConfig.BufferSize,
                             FileOptions.Asynchronous | FileOptions.SequentialScan);

                try
                {
                    await fs.CopyToAsync(Stream.Null, FileReadConfig.BufferSize, token);
                    await Task.Delay(Program.delayMilliseconds, token);

                    Ui.WriteStatus($"{Path.GetFileName(item.Path)} 를 읽어왔습니다. (I/O 비동기)");
                }
                finally
                {
                    if (item.PreOpenedStream is null)
                    {
                        await fs.DisposeAsync();
                    }
                }
            }
            finally
            {
            }
        }
    }
}
