﻿using MediaDevices;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace UnloadCamera
{
    class ViewModel : INotifyPropertyChanged
    {
        public ImageSource IconAsImage => Imaging.CreateBitmapSourceFromHIcon(
            Icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

        public Icon Icon=> Properties.Resources.Camera;

        public ObservableCollection<string> LogEntries { get; } = new ObservableCollection<string>();

        public int LogEntriesLastIndex => LogEntries.Count - 1;

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
                TriggerPropertyChanged(nameof(LogEntriesLastIndex));
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

                                        foreach (var file in files)
                                        {
                                            var date = dev.GetFileInfo(file).LastWriteTime ?? DateTime.Now;
                                            int increment = 0;
                                            string destPath;
                                            do
                                            {
                                                destPath = Path.Combine(settings.TargetDirectory, $"{date.Year}", $"{date.Year}{date.Month:00}", Path.GetFileName(file));
                                                increment++;
                                            } while (File.Exists(destPath));

                                            Log($"Copying {file} to {destPath}");
                                            Directory.CreateDirectory(Path.GetDirectoryName(destPath));
                                            using (var outputFile = File.OpenWrite(destPath))
                                            {
                                                dev.DownloadFile(file, outputFile);
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
