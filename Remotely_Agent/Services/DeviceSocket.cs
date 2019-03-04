﻿using Remotely_Agent.Client;
using Remotely_Library.Models;
using Remotely_Library.Services;
using Remotely_Library.Win32;
using Remotely_Library.Win32_Classes;
using Microsoft.AspNetCore.SignalR.Client;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Timers;
using System.Reflection;

namespace Remotely_Agent.Services
{
    public static class DeviceSocket
    {
        public static Timer HeartbeatTimer { get; private set; }
        public static bool IsServerVerified { get; set; }
        private static ConnectionInfo ConnectionInfo { get; set; }

        private static HubConnection HubConnection { get; set; }

        public static async void Connect()
        {
            ConnectionInfo = Utilities.GetConnectionInfo();

            HubConnection = new HubConnectionBuilder()
                .WithUrl(ConnectionInfo.Host + "/DeviceHub")
                .Build();
            HubConnection.Closed += HubConn_Closed;

            RegisterMessageHandlers(HubConnection);

            await HubConnection.StartAsync();

            var device = Device.Create(ConnectionInfo);

            await HubConnection.InvokeAsync("DeviceCameOnline", device);

            if (string.IsNullOrWhiteSpace(ConnectionInfo.ServerVerificationToken))
            {
                IsServerVerified = true;
                ConnectionInfo.ServerVerificationToken = Guid.NewGuid().ToString();
                await HubConnection.InvokeAsync("SetServerVerificationToken", ConnectionInfo.ServerVerificationToken);
                Utilities.SaveConnectionInfo(ConnectionInfo);
                Updater.CheckForCoreUpdates();
            }
            else
            {
                await HubConnection.InvokeAsync("SendServerVerificationToken");
            }

            if (HeartbeatTimer != null)
            {
                HeartbeatTimer.Stop();
            }
            HeartbeatTimer = new Timer(300000);
            HeartbeatTimer.Elapsed += HeartbeatTimer_Elapsed;
            HeartbeatTimer.Start();
        }

        public static void SendHeartbeat()
        {
            var currentInfo = Device.Create(ConnectionInfo);
            HubConnection.InvokeAsync("DeviceHeartbeat", currentInfo);
        }

        private static async Task ExecuteCommand(string mode, string command, string commandID, string senderConnectionID)
        {
            if (!IsServerVerified)
            {
                Logger.Write($"Command attempted before server was verified.  Mode: {mode}.  Command: {command}.  Sender: {senderConnectionID}");
                Uninstaller.UninstallClient();
                return;
            }
            try
            {
                switch (mode.ToLower())
                {
                    case "pscore":
                        {
                            var psCoreResult = PSCore.GetCurrent(senderConnectionID).WriteInput(command, commandID);
                            var serializedResult = JsonConvert.SerializeObject(psCoreResult);
                            if (Encoding.UTF8.GetBytes(serializedResult).Length > 400000)
                            {
                                SendResultsViaAjax("PSCore", psCoreResult);
                                await HubConnection.InvokeAsync("PSCoreResultViaAjax", commandID);
                            }
                            else
                            {
                                await HubConnection.InvokeAsync("PSCoreResult", psCoreResult);
                            }
                            break;
                        }

                    case "winps":
                        if (OSUtils.IsWindows)
                        {
                            var result = WindowsPS.GetCurrent(senderConnectionID).WriteInput(command, commandID);
                            var serializedResult = JsonConvert.SerializeObject(result);
                            if (Encoding.UTF8.GetBytes(serializedResult).Length > 400000)
                            {
                                SendResultsViaAjax("WinPS", result);
                                await HubConnection.InvokeAsync("WinPSResultViaAjax", commandID);
                            }
                            else
                            {
                                await HubConnection.InvokeAsync("CommandResult", result);
                            }
                        }
                        break;
                    case "cmd":
                        if (OSUtils.IsWindows)
                        {
                            var result = CMD.GetCurrent(senderConnectionID).WriteInput(command, commandID);
                            var serializedResult = JsonConvert.SerializeObject(result);
                            if (Encoding.UTF8.GetBytes(serializedResult).Length > 400000)
                            {
                                SendResultsViaAjax("CMD", result);
                                await HubConnection.InvokeAsync("CMDResultViaAjax", commandID);
                            }
                            else
                            {
                                await HubConnection.InvokeAsync("CommandResult", result);
                            }
                        }
                        break;
                    case "bash":
                        if (OSUtils.IsLinux)
                        {
                            var result = Bash.GetCurrent(senderConnectionID).WriteInput(command, commandID);
                            var serializedResult = JsonConvert.SerializeObject(result);
                            if (Encoding.UTF8.GetBytes(serializedResult).Length > 400000)
                            {
                                SendResultsViaAjax("Bash", result);
                            }
                            else
                            {
                                await HubConnection.InvokeAsync("CommandResult", result);
                            }
                        }
                        break;
                    default:
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.Write(ex);
                await HubConnection.InvokeAsync("DisplayConsoleMessage", $"There was an error executing the command.  It has been logged on the client device.", senderConnectionID);
            }
        }

        private static void HeartbeatTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            SendHeartbeat();
        }

        private static async Task HubConn_Closed(Exception arg)
        {
            await Task.Delay(new Random().Next(5000, 30000));
            Connect();
        }

        private static void RegisterMessageHandlers(HubConnection hubConnection)
        {
            hubConnection.On("ExecuteCommand", (async (string mode, string command, string commandID, string senderConnectionID) =>
            {
                await ExecuteCommand(mode, command, commandID, senderConnectionID);
            }));
            hubConnection.On("TransferFiles", async (string transferID, List<string> fileIDs, string requesterID) =>
            {
                Logger.Write($"File transfer started by {requesterID}.");
                var connectionInfo = Utilities.GetConnectionInfo();
                var sharedFilePath = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),"RemotelySharedFiles")).FullName;
                
                foreach (var fileID in fileIDs)
                {
                    var url = $"{connectionInfo.Host}/API/FileSharing/{fileID}";
                    var wr = WebRequest.CreateHttp(url);
                    var response = await wr.GetResponseAsync();
                    var cd = response.Headers["Content-Disposition"];
                    var filename = cd.Split(";").FirstOrDefault(x => x.Trim().StartsWith("filename")).Split("=")[1];
                    using (var rs = response.GetResponseStream())
                    {
                        using (var fs = new FileStream(Path.Combine(sharedFilePath, filename), FileMode.Create))
                        {
                            rs.CopyTo(fs);
                        }
                    }
                }
                await HubConnection.InvokeAsync("TransferCompleted", transferID, requesterID);
            });
            hubConnection.On("DeployScript", async (string mode, string fileID, string commandContextID, string requesterID) => {
                var connectionInfo = Utilities.GetConnectionInfo();
                var sharedFilePath = Directory.CreateDirectory(Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                        "Remotely",
                        "SharedFiles"
                    )).FullName;
                var webClient = new WebClient();

                var url = $"{connectionInfo.Host}/API/FileSharing/{fileID}";
                var wr = WebRequest.CreateHttp(url);
                var response = await wr.GetResponseAsync();
                var cd = response.Headers["Content-Disposition"];
                var filename = cd.Split(";").FirstOrDefault(x => x.Trim().StartsWith("filename")).Split("=")[1];
                using (var rs = response.GetResponseStream())
                {
                    using (var sr = new StreamReader(rs))
                    {
                        var result = await sr.ReadToEndAsync();
                        await ExecuteCommand(mode, result, commandContextID, requesterID);
                    }
                }
            });
            hubConnection.On("UninstallClient", () =>
            {
                Uninstaller.UninstallClient();
            });
          
            hubConnection.On("RemoteControl", async (string requesterID, string serviceID) =>
            {
                if (!IsServerVerified)
                {
                    Logger.Write("Remote control attempted before server was verified.");
                    Uninstaller.UninstallClient();
                    return;
                }
                try
                {
                    if (!OSUtils.IsWindows)
                    {
                        await hubConnection.InvokeAsync("DisplayConsoleMessage", $"Remote control is only supported on Windows at this time.", requesterID);
                        return;
                    }
                    // Cleanup old files.
                    foreach (var file in Directory.GetFiles(Path.GetTempPath(), "Remotely_ScreenCast*"))
                    {
                        try
                        {
                            File.Delete(file);
                        }
                        catch { }
                    }

                    // Get temp file name.
                    var count = 0;
                    var filePath = Path.Combine(Path.GetTempPath(), "Remotely_ScreenCast.exe");
                    while (File.Exists(filePath))
                    {
                        filePath = Path.Combine(Path.GetTempPath(), $"Remotely_ScreenCast{count}.exe");
                        count++;
                    }

                    // Extract ScreenCast.
                    using (var mrs = Assembly.GetExecutingAssembly().GetManifestResourceStream("Remotely_Agent.Resources.Remotely_ScreenCast.exe"))
                    {
                        using (var fs = new FileStream(filePath, FileMode.Create))
                        {
                            mrs.CopyTo(fs);
                        }
                    }

                    // Start ScreenCast.
                    await hubConnection.InvokeAsync("DisplayConsoleMessage", $"Starting remote control...", requesterID);
                    if (OSUtils.IsWindows)
                    {

                        if (Program.IsDebug)
                        {
                            Process.Start(filePath, $"-mode Unattended -requester {requesterID} -serviceid {serviceID} -host {Utilities.GetConnectionInfo().Host} -desktop default");
                        }
                        else
                        {
                            var result = Win32Interop.OpenInteractiveProcess(filePath + $" -mode Unattended -requester {requesterID} -serviceid {serviceID} -host {Utilities.GetConnectionInfo().Host} -desktop default", "default", true, out _);
                            if (!result)
                            {
                                await hubConnection.InvokeAsync("DisplayConsoleMessage", "Remote control failed to start on target device.", requesterID);
                            }
                        }

                    }
                    //else if (OSUtils.IsLinux)
                    //{
                    //    var users = OSUtils.StartProcessWithResults("users", "");
                    //    var username = users?.Split()?.FirstOrDefault()?.Trim();

                    //    Process.Start("sudo", $"-u {username} {rcBinaryPath} -mode Unattended -requester {requesterID} -serviceid {serviceID} -desktop default -hostname {Utilities.GetConnectionInfo().Host.Split("//").Last()}");
                    //}
                }
                catch
                {
                    await hubConnection.InvokeAsync("DisplayConsoleMessage", "Remote control failed to start on target device.", requesterID);
                    throw;
                }
            });
            hubConnection.On("CtrlAltDel", () =>
            {
                User32.SendSAS(false);
            });
          
            hubConnection.On("ServerVerificationToken", (string verificationToken) =>
            {
                if (verificationToken == Utilities.GetConnectionInfo().ServerVerificationToken)
                {
                    IsServerVerified = true;
                    Updater.CheckForCoreUpdates();
                }
                else
                {
                    Logger.Write($"Server sent an incorrect verification token.  Token Sent: {verificationToken}.");
                    Uninstaller.UninstallClient();
                    return;
                }
            });
        }
        private static void SendResultsViaAjax(string resultType, object result)
        {
            var targetURL = Utilities.GetConnectionInfo().Host + $"/API/Commands/{resultType}";
            var webRequest = WebRequest.CreateHttp(targetURL);
            webRequest.Method = "POST";

            using (var sw = new StreamWriter(webRequest.GetRequestStream()))
            {
                sw.Write(JsonConvert.SerializeObject(result));
            }
            webRequest.GetResponse();
        }
    }
}