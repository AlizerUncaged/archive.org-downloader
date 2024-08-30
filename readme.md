# 📚 Archive.org Downloader

Archive.org Downloader is a C# console application that allows you to download files from Archive.org efficiently and easily. It supports parallel downloads, resumable downloads, and provides a user-friendly interface with progress bars.

## ✨ Features

- 🚀 Parallel downloads (up to 10 simultaneous downloads)
- ⏸️ Resumable downloads
- 📊 Progress bars for each download
- 📦 File size display during download
- 🔄 URL decoding of file names
- ✅ Skips already downloaded files

## 🛠️ Requirements

- 🖥️ .NET 8.0, but this could be compiled with older versions of .NET!
- 📦 NuGet packages:
  - Pastel
  - AngleSharp
  - Spectre.Console

## 🚀 Installation

1. 📥 Clone this repository or download the source code.
2. 🖱️ Open the solution in Visual Studio or your preferred IDE.
3. 📦 Restore the NuGet packages.
4. 🔨 Build the solution.

## 🔧 Usage

1. 🏃‍♂️ Run the application.
2. 🔗 When prompted, enter the Archive.org URL you want to download from. For example:
   ```
   https://archive.org/download/example-collection
   ```
3. 📂 The application will start downloading the files to a `downloads` folder in the current directory.
4. 📊 Progress for each file will be displayed in the console.

## 🔍 How It Works

1. 🌐 The application fetches the HTML content of the provided Archive.org URL.
2. 🔍 It parses the HTML to extract the download links for each file.
3. ⬇️ Files are downloaded in parallel (up to 10 at a time) using `HttpClient`.
4. 📁 For each file, the application:
   - ✅ Checks if the file already exists and is complete.
   - ⏸️ Supports resuming partially downloaded files.
   - 📊 Shows a progress bar with percentage and file size information.

## 🤝 Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

## ⚠️ Disclaimer

This tool is for personal use only. Please respect Archive.org's terms of service and do not use this tool to download copyrighted material without permission.