﻿using RandM.RMLib;
using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Net.Sockets;
using System.Net;

namespace RandM.fTelnetProxy {
    public class WebSocketClientThread : RMThread {
        private int _ConnectionId = 0;
        private DateTime _DateConnected = DateTime.Now;
        private DateTime _DateLastKeypress = DateTime.Now;
        private string _Hostname = "no_hostname_yet";
        private int _Port = 0;
        private Socket _Socket = null;

        public WebSocketClientThread(Socket socket, int connectionId) {
            _Socket = socket;
            _ConnectionId = connectionId;
        }

        protected override void Dispose(bool disposing) {
            if (!_Disposed) {
                if (disposing) {
                    // dispose managed state (managed objects).
                }

                // free unmanaged resources (unmanaged objects)
                // set large fields to null.

                // Call the base dispose
                base.Dispose(disposing);
            }
        }

        public void DisplayConnectionInformation() {
            RMLog.Info($"[{_ConnectionId}] {((IPEndPoint)_Socket.RemoteEndPoint).Address.ToString()} -> {_Hostname}:{_Port} (connected: {Math.Round(DateTime.Now.Subtract(_DateConnected).TotalMinutes, 1)}min, idle: {Math.Floor(DateTime.Now.Subtract(_DateLastKeypress).TotalSeconds)}sec)");
        }

        protected override void Execute() {
            try {
                // Handle non-proxy connections
                using (WebSocketConnection UserConnection = new WebSocketConnection(true, Config.Default.Certificate)) {
                    if (UserConnection.Open(_Socket)) {
                        RMLog.Debug("{" + _ConnectionId.ToString() + "} Opened connection from " + UserConnection.GetRemoteIP() + ":" + UserConnection.GetRemotePort());
                        if (UserConnection.Header["Path"] == "/ping") {
                            // Handle ping requests (from proxy.ftelnet.ca most likely)
                            string Ping = UserConnection.ReadLn(1000);
                            if (UserConnection.ReadTimedOut) {
                                RMLog.Debug("Answering a /ping (no time received) from " + UserConnection.GetRemoteIP() + ":" + UserConnection.GetRemotePort());
                            } else {
                                RMLog.Debug("Answering a /ping (" + Ping + ") from " + UserConnection.GetRemoteIP() + ":" + UserConnection.GetRemotePort());
                                UserConnection.Write(Ping);
                            }
                            return;
                        }
                    } else {
                        if (UserConnection.FlashPolicyFileRequest) {
                            RMLog.Info("{" + _ConnectionId.ToString() + "} Answered flash policy file request from " + UserConnection.GetRemoteIP() + ":" + UserConnection.GetRemotePort().ToString());
                        } else {
                            RMLog.Debug("{" + _ConnectionId.ToString() + "} Invalid WebSocket connection from " + UserConnection.GetRemoteIP() + ":" + UserConnection.GetRemotePort().ToString());
                        }
                        return;
                    }

                    // If we get here it's a proxy connection, so handle it
                    RMLog.Info("{" + _ConnectionId.ToString() + "} Connection accepted from " + UserConnection.GetRemoteIP() + ":" + UserConnection.GetRemotePort());

                    string MessageText = string.Format("{0}\t{1}\t{2}\t{3}\t{4}\t{5}\r\n",
                        DateTime.Now.ToString(),
                        UserConnection.GetRemoteIP(),
                        UserConnection.GetRemotePort(),
                        UserConnection.Header["Path"],
                        UserConnection.Protocol,
                        UserConnection.SubProtocol);
                    FileUtils.FileAppendAllText(Path.Combine(ProcessUtils.StartupPath, "fTelnetProxy-Connections.log"), MessageText, Encoding.ASCII);

                    // Defaults for redirect location
                    _Hostname = Config.Default.TargetHostname;
                    _Port = Config.Default.TargetPort;

                    // Check if user is requesting a custom target
                    if (UserConnection.Header["Path"] != "/") {
                        bool CanRelay = false;

                        // Extract the requested host and port
                        string[] HostAndPort = UserConnection.Header["Path"].Split('/');
                        if ((HostAndPort.Length == 3) && (int.TryParse(HostAndPort[2], out _Port))) {
                            _Hostname = HostAndPort[1];
                            if (Config.Default.TargetHostname.ToLower().Trim() == _Hostname.ToLower().Trim()) {
                                // User is requesting the target defined by the proxy admin, so check if it's to an allowed port
                                CanRelay = ((_Port > 0) && (_Port == Config.Default.TargetPort) || (_Port == Config.Default.RLoginPort));
                            } else if (!string.IsNullOrEmpty(Config.Default.RelayFilename)) {
                                // proxy admin has relaying enabled, so check against the relay.cfg file
                                try {
                                    // Read relay file
                                    if (File.Exists(Config.Default.RelayFilename)) {
                                        string[] AllowedHosts = File.ReadAllLines(Config.Default.RelayFilename);
                                        if (AllowedHosts.Length > 0) {
                                            // Check for a whitelisted port
                                            string[] AllowedPorts = AllowedHosts[0].Split(',');
                                            foreach (string AllowedPort in AllowedPorts) {
                                                if (AllowedPort == _Port.ToString()) {
                                                    CanRelay = true;
                                                    break;
                                                }
                                            }

                                            // Not a whitelisted port, check for a whitelisted host
                                            if (!CanRelay) {
                                                string RequestedHostPort = _Hostname.ToLower() + ":" + _Port.ToString();
                                                foreach (string AllowedHost in AllowedHosts) {
                                                    if (AllowedHost.Trim().ToLower() == RequestedHostPort) {
                                                        CanRelay = true;
                                                        break;
                                                    }
                                                }
                                            }
                                        } else {
                                            RMLog.Error("{" + _ConnectionId.ToString() + "} Relay file is empty: '" + Config.Default.RelayFilename + "'");
                                        }
                                    } else {
                                        RMLog.Error("{" + _ConnectionId.ToString() + "} Relay file does not exist: '" + Config.Default.RelayFilename + "'");
                                    }
                                } catch (Exception ex) {
                                    RMLog.Exception(ex, "{" + _ConnectionId.ToString() + "} Error reading relay file: '" + Config.Default.RelayFilename + "'");
                                }
                            }
                        }

                        if (!CanRelay) {
                            RMLog.Info("{" + _ConnectionId.ToString() + "} Rejecting request for " + _Hostname + ":" + _Port.ToString());
                            UserConnection.WriteLn("Sorry, for security reasons this proxy won't connect to " + _Hostname + ":" + _Port.ToString());
                            Thread.Sleep(2500);
                            return;
                        }
                    }

                    // Try to connect to the desired Host and Port
                    UserConnection.Write(Ansi.ClrScr() + "Connecting to " + _Hostname + ":" + _Port.ToString() + "...");
                    using (TcpConnection ServerConnection = new TcpConnection()) {
                        if (ServerConnection.Connect(_Hostname, _Port)) {
                            RMLog.Info("{" + _ConnectionId.ToString() + "} Connected to " + _Hostname + ":" + _Port.ToString());
                            UserConnection.WriteLn("connected!");

                            // Repeatedly move data around until a connection is closed (or a stop is requested)
                            bool DoSleep = true;
                            while (!_Stop && UserConnection.Connected && ServerConnection.Connected) {
                                DoSleep = true;

                                if (UserConnection.CanRead()) {
                                    ServerConnection.WriteBytes(UserConnection.ReadBytes());
                                    _DateLastKeypress = DateTime.Now;
                                    DoSleep = false;
                                }

                                if (ServerConnection.CanRead()) {
                                    UserConnection.WriteBytes(ServerConnection.ReadBytes());
                                    DoSleep = false;
                                }

                                if (DoSleep) Thread.Sleep(1);
                            }

                            // Check why we exited the loop
                            if (_Stop) {
                                RMLog.Info("{" + _ConnectionId.ToString() + "} Stop requested");
                                UserConnection.Write(Ansi.GotoXY(1, 1) + Ansi.CursorDown(255) + "\r\nProxy server shutting down...");
                                Thread.Sleep(2500);
                            } else if (!UserConnection.Connected) {
                                RMLog.Info("{" + _ConnectionId.ToString() + "} Client closed connection");
                            } else if (!ServerConnection.Connected) {
                                RMLog.Info("{" + _ConnectionId.ToString() + "} Server closed connection");
                                UserConnection.Write(Ansi.GotoXY(1, 1) + Ansi.CursorDown(255) + "\r\nServer closed connection...");
                                Thread.Sleep(2500);
                            }
                        } else {
                            RMLog.Info("{" + _ConnectionId.ToString() + "} Unable to connect to " + _Hostname + ":" + _Port.ToString());
                            UserConnection.WriteLn("unable to connect!");
                            Thread.Sleep(2500);
                        }
                    }
                }
            } catch (Exception ex) {
                RMLog.Exception(ex, "{" + _ConnectionId.ToString() + "} Exception in client thread");
            }
        }
    }
}
