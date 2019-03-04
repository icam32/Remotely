﻿using Microsoft.AspNetCore.SignalR.Client;
using Remotely_ScreenCast;
using Remotely_ScreenCast.Capture;
using Remotely_ScreenCast.Enums;
using Remotely_ScreenCast.Models;
using Remotely_ScreenCast.Sockets;
using Remotely_ScreenCast.Utilities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Win32;

namespace Remotely_ScreenCast
{
	public class Program
	{
        public static ICapturer Capturer { get; private set; }
        public static CaptureMode CaptureMode { get; private set; }
        public static bool DisconnectRequested { get; set; }
        public static AppMode Mode { get; private set; }
        public static string RequesterID { get; private set; }
        public static string ServiceID { get; private set; }
        public static string Host { get; private set; }
        public static HubConnection Connection { get; private set; }
        public static OutgoingMessages OutgoingMessages { get; private set; }
        public static ConcurrentDictionary<string, Viewer> Viewers { get; } = new ConcurrentDictionary<string, Viewer>();
        private static string CurrentDesktopName { get; set; }

        static void Main(string[] args)
        {
            try
            {
                AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

                var argDict = ProcessArgs(args);
                Mode = (AppMode)Enum.Parse(typeof(AppMode), argDict["mode"]);
                RequesterID = argDict["requester"];
                Host = argDict["host"];
                CurrentDesktopName = argDict["desktop"];
                ServiceID = argDict["serviceid"];

                Connection = new HubConnectionBuilder()
                    .WithUrl($"{Host}/RCDeviceHub")
                    .Build();

                Connection.StartAsync().Wait();

                var desktopName = Win32Interop.GetCurrentDesktop();
                if (desktopName.ToLower() != CurrentDesktopName.ToLower())
                {
                    RelaunchInCurrentDesktop(desktopName).Wait();
                    Environment.Exit(0);
                }

                OutgoingMessages = new OutgoingMessages(Connection);

                MessageHandlers.ApplyConnectionHandlers(Connection, OutgoingMessages);

                CursorIconWatcher.Current.OnChange += CursorIconWatcher_OnChange;

                if (argDict.ContainsKey("desktopswitch"))
                {
                    var viewersString = argDict["viewers"];
                    var viewerIDs = viewersString.Split(",".ToCharArray());
                    foreach (var id in viewerIDs)
                    {
                        ScreenCaster.BeginScreenCasting(Connection, id, null, OutgoingMessages);
                    }
                    // TODO.
                }
                else
                {
                    OutgoingMessages.NotifyRequesterUnattendedReady(RequesterID).Wait();
                }

                StartWaitForViewerTimer();

                while (true)
                {
                    if (Mode == AppMode.Unattended)
                    {
                        desktopName = Win32Interop.GetCurrentDesktop();
                        if (desktopName.ToLower() != CurrentDesktopName.ToLower())
                        {
                            SwitchDesktops(desktopName).Wait();
                            Environment.Exit(0);
                        }
                        System.Threading.Thread.Sleep(100);
                    }
                    else
                    {
                        Console.Read();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Write(ex);
            }
        }

        private static async Task RelaunchInCurrentDesktop(string desktopName)
        {
            var result = Win32Interop.OpenInteractiveProcess(Assembly.GetExecutingAssembly().Location + $" -mode {Mode.ToString()} -requester {RequesterID} -serviceid {ServiceID} -host {Host} -desktop {desktopName}", desktopName, true, out _);
            if (!result)
            {
                // TODO.
                //await Connection.InvokeAsync("DisplayMessage", "Remote control failed to start on target device.", RequesterID);
            }
            await Task.Delay(1);
        }

        private static async Task SwitchDesktops(string desktopName)
        {
            Logger.Write($"Switching desktops to {desktopName}.");
            await Connection.InvokeAsync("SwitchDesktops");
            var result = Win32Interop.OpenInteractiveProcess(Assembly.GetExecutingAssembly().Location + $" -mode {Mode.ToString()} -requester {RequesterID} -serviceid {ServiceID} -host {Host} -desktopswitch true -desktop {desktopName} -viewers {String.Join(",", Viewers.Keys.ToList())}", desktopName, true, out _);
            if (!result)
            {
                // TODO.
                //await Connection.InvokeAsync("DisplayMessage", "Desktop switch failed on target device.", RequesterID);
            }
        }

        private static async void CursorIconWatcher_OnChange(object sender, int cursor)
        {
            await OutgoingMessages.SendCursorChange(cursor, Viewers.Keys.ToList());
        }

        private static void StartWaitForViewerTimer()
        {
            var timer = new System.Timers.Timer(5000);
            timer.AutoReset = false;
            timer.Elapsed += (sender, arg) =>
            {
                // Shut down if no viewers have connected within 5 seconds.
                if (Viewers.Count == 0)
                {
                    Logger.Write("No viewers connected after 5 seconds.  Shutting down.");
                    Environment.Exit(0);
                }
            };
            timer.Start();
        }

        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Logger.Write((Exception)e.ExceptionObject);
        }

        private static Dictionary<string, string> ProcessArgs(string[] args)
        {
            var argDict = new Dictionary<string, string>();

            for (var i = 0; i < args.Length; i += 2)
            {
                var key = args?[i];
                if (key != null)
                {
                    key = key.Trim().Replace("-", "").ToLower();
                    var value = args?[i + 1];
                    if (value != null)
                    {
                        argDict[key] = args[i + 1].Trim();
                    }
                }

            }
            return argDict;
        }

    
    }
}