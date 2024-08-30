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

        if (percentage >= 100)
            progress.IsCompleted = true;
    }

    static (List<Progress>, List<Progress>, List<Progress>) CategorizeDownloads()
    {
        var completed = new List<Progress>();
        var inProgress = new List<Progress>();
        var errored = new List<Progress>();

        foreach (var progress in downloadProgress.Values)
        {
            if (progress.IsCompleted)
                completed.Add(progress);
            else if (progress.HasError)
                errored.Add(progress);
            else
                inProgress.Add(progress);
        }

        return (completed, inProgress, errored);
    }


    static void DrawProgress()
    {
        Console.Clear();
        var (completed, inProgress, errored) = CategorizeDownloads();

        var table = new Table().Expand().NoBorder();
        table.AddColumn("Completed Downloads");
        table.AddColumn("Downloads in Progress");

        table.AddRow(
            new Panel(GetCompletedDownloadsText(completed)).Expand(),
            new Panel(GetInProgressDownloadsText(inProgress)).Expand()
        );

        AnsiConsole.Write(table);

        if (errored.Count > 0)
        {
            AnsiConsole.WriteLine();
            DisplayErroredDownloads(errored);
        }
    }

    static string GetCompletedDownloadsText(List<Progress> completed)
    {
        var text = new System.Text.StringBuilder();
        int displayCount = Math.Min(completed.Count, 20);

        for (int i = 0; i < displayCount; i++)
        {
            var item = completed[i];
            string downloadedSize = FormatFileSize(item.DownloadedSize);
            text.AppendLine($"[green]■[/] {Markup.Escape(item.FileName)} ({downloadedSize})");
        }

        if (completed.Count > 20)
        {
            text.AppendLine($"[grey]...and {completed.Count - 20} more[/]");
        }

        return text.Length > 0 ? text.ToString() : "No completed downloads yet.";
    }

    static string GetInProgressDownloadsText(List<Progress> inProgress)
    {
        var text = new System.Text.StringBuilder();

        foreach (var item in inProgress)
        {
            string downloadedSize = FormatFileSize(item.DownloadedSize);
            string totalSize = FormatFileSize(item.TotalSize);
            string progressText = item.Percentage >= 0
                ? $"{item.Percentage:F2}% ({downloadedSize} / {totalSize})"
                : $"In progress ({downloadedSize} downloaded)";

            text.AppendLine(Markup.Escape($"{item.FileName}: {progressText}"));

            if (item.Percentage >= 0)
            {
                text.AppendLine(CreateProgressBar(item.Percentage));
            }
            else
            {
                text.AppendLine("[yellow]⋯⋯⋯⋯⋯⋯⋯⋯⋯⋯[/]");
            }

            text.AppendLine();
        }

        return text.Length > 0 ? text.ToString() : "No downloads in progress.";
    }

    static string CreateProgressBar(double percentage)
    {
        int width = 20;
        int filledWidth = (int)Math.Round(percentage / 100 * width);
        int emptyWidth = width - filledWidth;

        return $"[green]{new string('█', filledWidth)}[/][grey]{new string('░', emptyWidth)}[/]";
    }

    static void DisplayErroredDownloads(List<Progress> errored)
    {
        var panel = new Panel(GetErroredDownloadsText(errored))
            .Header("Errored Downloads")
            .Expand();

        AnsiConsole.Write(panel);
    }

    static string GetErroredDownloadsText(List<Progress> errored)
    {
        var text = new System.Text.StringBuilder();

        foreach (var item in errored)
        {
            text.AppendLine($"[red]■[/] {Markup.Escape(item.FileName)}");
        }

        return text.Length > 0 ? text.ToString() : "No errored downloads.";
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
        public bool IsCompleted { get; set; }
        public bool HasError { get; set; }


        public Progress(string fileName)
        {
            FileName = fileName;
            Percentage = 0;
        }
    }
}