﻿/* Copyright (c) Citrix Systems Inc.
 * All rights reserved.
 *
 * Redistribution and use in source and binary forms, 
 * with or without modification, are permitted provided 
 * that the following conditions are met:
 *
 * *   Redistributions of source code must retain the above 
 *     copyright notice, this list of conditions and the 
 *     following disclaimer.
 * *   Redistributions in binary form must reproduce the above 
 *     copyright notice, this list of conditions and the 
 *     following disclaimer in the documentation and/or other 
 *     materials provided with the distribution.
 *
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND 
 * CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, 
 * INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF 
 * MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE 
 * DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR 
 * CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, 
 * SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, 
 * BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR 
 * SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS 
 * INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, 
 * WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING 
 * NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE 
 * OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF 
 * SUCH DAMAGE.
 */

using System;
using System.Diagnostics;
using System.Threading;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Collections.Generic;
using TaskScheduler;

namespace XenUpdater
{
    class AutoUpdate
    {
        XenStoreSession session;
        XenStoreItem licensed;
        XenStoreItem enabled;
        XenStoreItem update_url;
        Version version;

        private object GetReg(string key, string name, object def)
        {
            try
            {
                object obj = Registry.GetValue(key, name, def);
                if (obj == null)
                    return def;
                return obj;
            }
            catch
            {
                return def;
            }
        }

        public AutoUpdate()
        {
            session = new XenStoreSession("CheckNow");
            licensed = new XenStoreItem(session, "/guest_agent_features/Guest_agent_auto_update/licensed");
            enabled = new XenStoreItem(session, "/guest_agent_features/Guest_agent_auto_update/parameters/enabled");
            update_url = new XenStoreItem(session, "/guest_agent_features/Guest_agent_auto_update/parameters/update_url");

            int major = (int)GetReg("HKEY_LOCAL_MACHINE\\SOFTWARE\\Citrix\\XenTools", "MajorVersion", 0); 
            int minor = (int)GetReg("HKEY_LOCAL_MACHINE\\SOFTWARE\\Citrix\\XenTools", "MinorVersion", 0);
            int micro = (int)GetReg("HKEY_LOCAL_MACHINE\\SOFTWARE\\Citrix\\XenTools", "MicroVersion", 0);
            int build = (int)GetReg("HKEY_LOCAL_MACHINE\\SOFTWARE\\Citrix\\XenTools", "BuildNumber", 0);
            version = new Version(major, minor, micro, build);
        }

        public void CheckNow()
        {
            if (!licensed.Exists)
                return;
            if (licensed.Value != "1")
                return;
            if (!enabled.Exists)
                return;
            if (enabled.Value != "1")
                return;
            if ((int)GetReg("HKEY_LOCAL_MACHINE\\SOFTWARE\\Citrix\\XenTools", "DisableAutoUpdate", 0) != 0)
                return;
            Version minver = new Version(6, 5, 0, 0);
            if (minver.CompareTo(version) > 0)
                return;

            // Do the Update
            Update update = CheckForUpdates();
            if (update == null)
                return;

            string temp = DownloadUpdate(update);
            if (String.IsNullOrEmpty(temp))
                return;

            string target = GetTarget();
            ProcessStartInfo start = new ProcessStartInfo();
            start.FileName = "msiexec.exe";
            start.CreateNoWindow = true;
            start.UseShellExecute = false;
            start.RedirectStandardError = true;
            start.RedirectStandardOutput = true;
            start.Arguments = " /i \"" + temp + "\" TARGETDIR=\"" + target + "\" /log \"" + Path.Combine(target, "agent3log.log") + "\" /qn";

            foreach (var process in Process.GetProcessesByName("XenDPriv.exe"))
                process.Kill();

            Process proc = Process.Start(start);
        }

        private Update CheckForUpdates()
        {
            string url = "https://xenserver-windows-agent-cfu.s3.amazonaws.com/updates.tsv";
            if (update_url.Exists)
                url = update_url.Value;
            url = (string)GetReg("HKEY_LOCAL_MACHINE\\SOFTWARE\\Citrix\\XenTools", "update_url", url);

            WebClient client = new WebClient();
            string contents = client.DownloadString(url);

            string arch = (Win32Impl.Is64BitOS() && (!Win32Impl.IsWOW64())) ? "x64" : "x86";
            List<Update> updates = new List<Update>();
            foreach (string line in contents.Split(new char[] { '\n' }))
            {
                Update update = new Update(line);
                if (update.Arch != arch)
                    continue;
                if (version.CompareTo(update.Version) >= 0)
                    continue;

                updates.Add(update);
            }

            updates.Reverse();
            if (updates.Count > 0)
                return updates[0];
            return null;
        }

        private string DownloadUpdate(Update update)
        {
            string temp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                       update.FileName);
            try
            {
                if (File.Exists(temp))
                    File.Delete(temp);

                WebClient client = new WebClient();
                client.DownloadFile(update.Url, temp);

                // validate checksum
                if (!VerifyCertificate(temp))
                    throw new Exception("Invalid Certificate");

                return temp;
            }
            catch
            {
                if (File.Exists(temp))
                    File.Delete(temp);
                return null;
            }
        }

        private bool VerifyCertificate(string filename)
        {
            try
            {
                X509Certificate signer = X509Certificate.CreateFromSignedFile(filename);
                if (!signer.Subject.Contains("O=\"Citrix Systems, Inc.\""))
                    return false;
                X509Certificate2 cert = new X509Certificate2(signer);
                X509Chain chain = new X509Chain();
                chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
                chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
                chain.ChainPolicy.UrlRetrievalTimeout = new TimeSpan(0, 1, 0);
                chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
                return chain.Build(cert);
            }
            catch
            {
                return false;
            }
        }

        private string GetTarget()
        {
            //string defaultPath = Application.StartupPath;
            string defaultPath = Environment.CurrentDirectory;
            string regPath = (Win32Impl.Is64BitOS()) ? 
                            "HKEY_LOCAL_MACHINE\\SOFTWARE\\Wow6432Node\\Citrix\\XenToolsInstaller" :
                            "HKEY_LOCAL_MACHINE\\SOFTWARE\\Citrix\\XenToolsInstaller";
            return (string)Registry.GetValue(regPath, "Install_Dir", defaultPath);
        }

        class Update
        {
            internal string Arch { get; private set; }
            internal string FileName { get; private set; }
            internal string Url { get; private set; }
            internal Version Version { get; private set; }
            internal int Size { get; private set; }
            internal int Checksum { get; private set; }

            internal Update(string line)
            {
                // assume line is JSON or something
            }
        }

        class Win32Impl
        {
            static public bool Is64BitOS()
            {
                if (IntPtr.Size == 8)
                    return true;
                return IsWOW64();
            }

            static public bool IsWOW64()
            {
                bool flags;
                IntPtr modhandle = GetModuleHandle("kernel32.dll");
                if (modhandle == IntPtr.Zero)
                    return false;
                if (GetProcAddress(modhandle, "IsWow64Process") == IntPtr.Zero)
                    return false;

                if (IsWow64Process(GetCurrentProcess(), out flags))
                    return flags;
                return false;
            }

            [DllImport("kernel32.dll")]
            static extern IntPtr GetCurrentProcess();

            [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
            static extern IntPtr GetModuleHandle(string moduleName);

            [DllImport("kernel32", CharSet = CharSet.Auto, SetLastError = true)]
            static extern IntPtr GetProcAddress(IntPtr hModule,
                [MarshalAs(UnmanagedType.LPStr)]string procName);

            [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            static extern bool IsWow64Process(IntPtr hProcess, out bool wow64Process);
        }
    }
}
