﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Windows.Forms;
using EnvDTE;
using ExtensionManager.Importer;
using Microsoft;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ExtensionManager;
using Microsoft.VisualStudio.Setup.Configuration;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace ExtensionManager
{
    internal sealed class ImportCommand
    {
        private readonly Package _package;
        private readonly ExtensionService _es;

        private ImportCommand(Package package, OleMenuCommandService commandService, ExtensionService es)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
            _es = es;

            var cmdId = new CommandID(PackageGuids.guidExportPackageCmdSet, PackageIds.ImportCmd);
            var cmd = new MenuCommand(Execute, cmdId);
            commandService.AddCommand(cmd);
        }

        public static ImportCommand Instance { get; private set; }

        private IServiceProvider ServiceProvider
        {
            get { return _package; }
        }

        public static void Initialize(AsyncPackage package, OleMenuCommandService commandService, ExtensionService es)
        {
            Instance = new ImportCommand(package, commandService, es);
        }

        private void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!TryGetFilePath(out string filePath))
            {
                return;
            }

            var manifest = Manifest.FromFile(filePath);
            manifest.MarkSelected(_es.GetInstalledExtensions());

            var dialog = ImportWindow.Open(manifest.Extensions, Purpose.Install);

            if (dialog.DialogResult == true && dialog.SelectedExtension.Any())
            {
                var toInstall = dialog.SelectedExtension.Select(ext => ext.ID).ToList() ;

                var repository = ServiceProvider.GetService(typeof(SVsExtensionRepository)) as IVsExtensionRepository;
                Assumes.Present(repository);

                IEnumerable<GalleryEntry> marketplaceEntries = repository.GetVSGalleryExtensions<GalleryEntry>(toInstall, 1033, false);
                string tempDir = PrepareTempDir();

                var dte = ServiceProvider.GetService(typeof(DTE)) as DTE;
                Assumes.Present(dte);

                dte.StatusBar.Text = "Downloading extensions...";

                HasRootSuffix(out string rootSuffix);

                ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    await DownloadExtensionAsync(marketplaceEntries, tempDir);

                    InvokeVsixInstaller(tempDir, rootSuffix);

                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    dte.StatusBar.Text = "Extensions downloaded. Starting VSIX Installer...";
                });
            }
        }

        private static string PrepareTempDir()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), nameof(ExtensionManager));

            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }

            Directory.CreateDirectory(tempDir);
            return tempDir;
        }

        private void InvokeVsixInstaller(string tempDir, string rootSuffix)
        {
            var process = System.Diagnostics.Process.GetCurrentProcess();
            string dir = Path.GetDirectoryName(process.MainModule.FileName);
            string exe = Path.Combine(dir, "VSIXInstaller.exe");
            var configuration = new SetupConfiguration() as ISetupConfiguration;
            ISetupInstance instance = configuration.GetInstanceForCurrentProcess();
            IEnumerable<string> vsixFiles = Directory.EnumerateFiles(tempDir, "*.vsix").Select(f => Path.GetFileName(f));

            var start = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"{string.Join(" ", vsixFiles)} /instanceIds:{instance.GetInstanceId()}",
                WorkingDirectory = tempDir,
                UseShellExecute = false,
            };

            if (!string.IsNullOrEmpty(rootSuffix))
            {
                start.Arguments += $" /rootSuffix:{rootSuffix}";
            }

            System.Diagnostics.Process.Start(start);
        }

        private async Task DownloadExtensionAsync(IEnumerable<GalleryEntry> entries, string dir)
        {
            var tasks = new List<Task>();

            foreach (GalleryEntry entry in entries)
            {
                string localPath = Path.Combine(dir, Guid.NewGuid() + ".vsix");

                using (var client = new WebClient())
                {
                    Task task = client.DownloadFileTaskAsync(entry.DownloadUrl, localPath);
                    tasks.Add(task);
                }
            }

            await Task.WhenAll(tasks);
        }

        private bool TryGetFilePath(out string filePath)
        {
            filePath = null;

            using (var sfd = new OpenFileDialog())
            {
                sfd.DefaultExt = ".vsext";
                sfd.FileName = "extensions";
                sfd.Filter = "VSEXT File|*.vsext";

                DialogResult result = sfd.ShowDialog();

                if (result == DialogResult.OK)
                {
                    filePath = sfd.FileName;
                    return true;
                }
            }

            return false;
        }

        public static bool HasRootSuffix(out string rootSuffix)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            rootSuffix = null;

            if (Microsoft.VisualStudio.Shell.ServiceProvider.GlobalProvider.GetService(typeof(SVsAppCommandLine)) is IVsAppCommandLine appCommandLine)
            {
                if (ErrorHandler.Succeeded(appCommandLine.GetOption("rootsuffix", out int hasRootSuffix, out rootSuffix)))
                {
                    return hasRootSuffix != 0;
                }
            }

            return false;
        }
    }
}
