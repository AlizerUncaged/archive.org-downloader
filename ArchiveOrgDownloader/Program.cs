using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Web;
using AngleSharp.Html.Parser;
using Pastel;
using Spectre.Console;

namespace ArchiveOrgDownloader;

class Program
{
    private static readonly HttpClient httpClient = new HttpClient();

    private static readonly ConcurrentDictionary<string, Progress> downloadProgress =
        new ConcurrentDictionary<string, Progress>();

    private const int MaxConcurrentDownloads = 10;

    static async Task Main(string[] args)
    {
        Console.WriteLine("Welcome to the Archive.org Downloader!".Pastel("#00FF00"));
        Console.Write("Please enter the Archive.org URL: ".Pastel("#FFFF00"));
        string url = Console.ReadLine();

        string archiveName = ExtractArchiveName(url);
        string downloadPath = Path.Combine(".", "downloads", archiveName);
        Directory.CreateDirectory(downloadPath);

        Console.WriteLine($"Downloading files to: {downloadPath}".Pastel("#00FFFF"));

        List<(string url, string fileName)> filesToDownload = await GetFilesToDownload(url);


        using (var semaphore = new SemaphoreSlim(MaxConcurrentDownloads))
        {
            var downloadTasks = filesToDownload
                .Select(file => DownloadFileAsync(file.url, file.fileName, downloadPath, semaphore)).ToList();

            Console.Clear();
            var progressTask = Task.Run(async () =>
            {
                while (!downloadTasks.All(t => t.IsCompleted))
                {
                    DrawProgress();
                    await Task.Delay(700);
                }

                DrawProgress(); // Final update
            });

            await Task.WhenAll(downloadTasks.Concat(new[] { progressTask }));
        }


        Console.WriteLine("All downloads completed!".Pastel("#00FF00"));
    }

    static string ExtractArchiveName(string url)
    {
        var match = Regex.Match(url, @"https://archive\.org/download/([^/]+)");
        return match.Success ? match.Groups[1].Value : "unknown";
    }


    static async Task<List<(string url, string fileName)>> GetFilesToDownload(string url)
    {
        var response = await httpClient.GetStringAsync(url);
        var parser = new HtmlParser();
        var document = await parser.ParseDocumentAsync(response);

        var baseUri = new Uri(url);
        var fileLinks = document.QuerySelectorAll("table.directory-listing-table a")
            .Where(a => !a.TextContent.Contains("Parent Directory"))
            .Select(a =>
            {
                var href = a.GetAttribute("href");
                var fullUrl = url.TrimEnd('/') + "/" + href;
                var fileName = HttpUtility.UrlDecode(Path.GetFileName(href));
                return (url: fullUrl, fileName: fileName);
            })
            .ToList();

        return fileLinks;
    }


    static async Task DownloadFileAsync(string url, string fileName, string downloadPath, SemaphoreSlim semaphore)
    {
        await semaphore.WaitAsync();

        try
        {
            fileName = string.IsNullOrWhiteSpace(fileName) ? "UNNAMED" : fileName;
            string filePath = Path.Combine(downloadPath, fileName);
            long existingFileSize = 0;

            if (File.Exists(filePath))
            {
                existingFileSize = new FileInfo(filePath).Length;
                var response = await httpClient.SendAsync(new HttpRequestMessage(HttpMethod.Head, url));
                var totalSize = response.Content.Headers.ContentLength.GetValueOrDefault();

                if (existingFileSize == totalSize)
                {
                    Console.WriteLine($"File {fileName} is already fully downloaded. Skipping.".Pastel("#FFFF00"));
                    return;
                }
            }

            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                if (existingFileSize > 0)
                {
                    request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existingFileSize, null);
                }

                using (var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();

                    var totalSize = response.Content.Headers.ContentLength.GetValueOrDefault() + existingFileSize;

                    if (Directory.Exists(filePath)) // this happen
                        filePath = Path.Combine(filePath, "dir_file");

                    using (var contentStream = await response.Content.ReadAsStreamAsync())
                    using (var fileStream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.None))
                    {
                        var buffer = new byte[81920];
                        long totalBytesRead = 0;
                        int bytesRead;

                        while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        {
                            await fileStream.WriteAsync(buffer, 0, bytesRead);
                            totalBytesRead += bytesRead;

                            double percentage;
                            if (totalSize > 0)
                            {
                                percentage = (totalBytesRead + existingFileSize) * 100.0 / totalSize;
                            }
                            else
                            {
                                // If we don't know the total size, use an indeterminate progress
                                percentage = -1;
                            }

                            UpdateProgress(fileName, percentage, totalSize, totalBytesRead + existingFileSize);
                        }
                    }
                }
            }

            Console.WriteLine($"Download completed: {fileName}".Pastel("#00FF00"));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error downloading {fileName} at {url}: {ex.Message}".Pastel("#FF0000"));
        }
        finally
        {
            semaphore.Release();
        }
    }

    static void UpdateProgress(string fileName, double percentage, long totalSize, long downloadedSize)
    {
        if (!downloadProgress.TryGetValue(fileName, out var progress))
        {
            progress = new Progress(fileName);
            downloadProgress[fileName] = progress;
        }

        progress.Percentage = percentage;
        progress.TotalSize = totalSize;
        progress.DownloadedSize = downloadedSize;
    }

    static void DrawProgress()
    {
        Console.Clear();
        foreach (var progress in downloadProgress.Values)
        {
            string progressText;
            if (progress.Percentage >= 0)
            {
                string downloadedSize = FormatFileSize(progress.DownloadedSize);
                string totalSize = FormatFileSize(progress.TotalSize);
                progressText = $"{progress.Percentage:F2}% ({downloadedSize} / {totalSize})";
            }
            else
            {
                string downloadedSize = FormatFileSize(progress.DownloadedSize);
                progressText = $"In progress ({downloadedSize} downloaded)";
            }

            AnsiConsole.MarkupLine("[bold]" + Markup.Escape($"{progress.FileName}") + $"[/]: {progressText}");

            if (progress.Percentage >= 0)
            {
                AnsiConsole.Progress()
                    .HideCompleted(false)
                    .Start(ctx =>
                    {
                        var task = ctx.AddTask("[green]Downloading[/]");
                        task.Value = progress.Percentage;
                    });
            }
            else
            {
                AnsiConsole.MarkupLine("[green]Downloading...[/]");
            }
        }
    }

    static string FormatFileSize(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        int counter = 0;
        decimal number = bytes;
        while (Math.Round(number / 1024) >= 1)
        {
            number /= 1024;
            counter++;
        }

        return $"{number:n1} {suffixes[counter]}";
    }


    class Progress
    {
        public string FileName { get; set; }
        public double Percentage { get; set; }
        public long TotalSize { get; set; }
        public long DownloadedSize { get; set; }

        public Progress(string fileName)
        {
            FileName = fileName;
            Percentage = 0;
        }
    }
}