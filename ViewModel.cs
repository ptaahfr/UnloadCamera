using MediaDevices;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace UnloadCamera
{
    class ViewModel : INotifyPropertyChanged
    {
        public ImageSource IconAsImage => Imaging.CreateBitmapSourceFromHIcon(
            Icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

        public Icon Icon=> Properties.Resources.Camera;

        public ObservableCollection<string> LogEntries { get; } = new ObservableCollection<string>();

        public int LogEntriesIndex => LogEntries.Count - 1;

        public event PropertyChangedEventHandler PropertyChanged;

        void TriggerPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        void Log(string str)
        {
            mainSyncContext_.Post((_) =>
            {
                LogEntries.Add(str);
                if (LogEntries.Count > 256)
                {
                    LogEntries.RemoveAt(0);
                }
                TriggerPropertyChanged(nameof(LogEntries));
                TriggerPropertyChanged(nameof(LogEntriesIndex));
            }, null);
        }

        Task task_;
        SynchronizationContext mainSyncContext_ = SynchronizationContext.Current;
        ManualResetEvent requestFinish_ = new ManualResetEvent(false);
        ManualResetEvent finished_ = new ManualResetEvent(false);

        ~ViewModel()
        {
            requestFinish_.Set();
            finished_.WaitOne();
        }

        public ViewModel()
        {
            HashSet<string> checkedDevices = new HashSet<string>();

            task_ = Task.Factory.StartNew(delegate
            {
                for (; requestFinish_.WaitOne(1000) == false;)
                {
                    try
                    {
                        var settings = Properties.Settings.Default;
                        var deviceNames = settings.DeviceNames.Split(',');
                        var devices = MediaDevice.GetDevices();

                        foreach (var deviceName in deviceNames)
                        {
                            if (devices.All(x => x.FriendlyName != deviceName.Split('\\').FirstOrDefault()))
                                checkedDevices.Remove(deviceName);
                        }

                        foreach (var dev in devices)
                        {
                            foreach (var deviceName in deviceNames)
                            {
                                if (deviceName.Split('\\').FirstOrDefault() == dev.FriendlyName)
                                {
                                    if (checkedDevices.Contains(deviceName))
                                        continue;

                                    checkedDevices.Add(deviceName);

                                    Log($"Checking device: {deviceName}");
                                    var subPath = deviceName.Split('\\').Skip(1).FirstOrDefault() ?? string.Empty;

                                    dev.Connect();
                                    try
                                    {
                                        var rootDir = Path.Combine(subPath, "DCIM");
                                        var files = dev.GetFiles(rootDir, "*.*", SearchOption.AllDirectories);
                                        var dates = files.Select(x => dev.GetFileInfo(x).LastWriteTime);
                                        foreach (var file in files)
                                        {
                                            var fileInfo = dev.GetFileInfo(file);
                                            var date = fileInfo.LastWriteTime ?? DateTime.Now;
                                            int increment = 0;
                                            string destPath;
                                            do
                                            {
                                                destPath = Path.Combine(settings.TargetDirectory, $"{date.Year}", $"{date.Year} - {date.Month:00}", Path.GetFileName(file));
                                                increment++;
                                            } while (dev.FileExists(destPath));

                                            dev.CreateDirectory(Path.GetDirectoryName(destPath));

                                            using (var tempStream = new MemoryStream())
                                            {
                                                Log($"Copying {file} to {destPath}");
                                                dev.DownloadFile(file, tempStream);
                                                Directory.CreateDirectory(Path.GetDirectoryName(destPath));
                                                using (var outputFile = File.OpenWrite(destPath))
                                                {
                                                    tempStream.Seek(0, SeekOrigin.Begin);
                                                    tempStream.CopyTo(outputFile);
                                                }

                                                if (new FileInfo(destPath).Length != tempStream.Length)
                                                {
                                                    Log($"Incomplete copying, skipping.");
                                                    continue;
                                                }
                                            }

                                            Log($"Deleting {file}");
                                            dev.DeleteFile(file);
                                        }
                                    }
                                    catch (Exception e)
                                    {
                                        Log($"Exception processing device {deviceName}: {e.Message}");
                                    }
                                    finally
                                    {
                                        dev.Disconnect();
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        Log($"Exception: {e.Message}");
                    }
                }

                finished_.Set();
            }, TaskCreationOptions.LongRunning);
        }
    }
}
