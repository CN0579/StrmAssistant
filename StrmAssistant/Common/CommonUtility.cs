﻿using MediaBrowser.Model.IO;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace StrmAssistant.Common
{
    public static class CommonUtility
    {
        private static readonly Regex MovieDbApiKeyRegex = new Regex("^[a-fA-F0-9]{32}$", RegexOptions.Compiled);

        public static bool IsValidHttpUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;

            if (Uri.TryCreate(url, UriKind.Absolute, out var uriResult))
            {
                return uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps;
            }

            return false;
        }

        public static bool IsValidMovieDbApiKey(string apiKey)
        {
            return !string.IsNullOrWhiteSpace(apiKey) && MovieDbApiKeyRegex.IsMatch(apiKey);
        }

        public static bool IsValidProxyUrl(string proxyUrl)
        {
            try
            {
                var uri = new Uri(proxyUrl);
                return (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
                       (uri.IsDefaultPort || (uri.Port > 0 && uri.Port <= 65535)) &&
                       (string.IsNullOrEmpty(uri.UserInfo) || uri.UserInfo.Contains(":"));
            }
            catch
            {
                return false;
            }
        }

        public static bool TryParseProxyUrl(string proxyUrl, out string schema, out string host, out int port, out string username, out string password)
        {
            schema = string.Empty;
            host = string.Empty;
            port = 0;
            username = string.Empty;
            password = string.Empty;

            try
            {
                var uri = new Uri(proxyUrl);
                if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                {
                    schema = uri.Scheme;
                    host = uri.Host;
                    port = uri.IsDefaultPort ? uri.Scheme == Uri.UriSchemeHttp ? 80 : 443 : uri.Port;

                    if (!string.IsNullOrEmpty(uri.UserInfo))
                    {
                        var userInfoParts = uri.UserInfo.Split(':');
                        username = userInfoParts[0];
                        password = userInfoParts.Length > 1 ? userInfoParts[1] : string.Empty;
                    }

                    return true;
                }
            }
            catch
            {
                // ignored
            }

            return false;
        }

        public static (bool isReachable, double? tcpPing) CheckProxyReachability(string host, int port)
        {
            try
            {
                using var tcpClient = new TcpClient();
                var stopwatch = Stopwatch.StartNew();
                if (tcpClient.ConnectAsync(host, port).Wait(999))
                {
                    stopwatch.Stop();
                    return (true, stopwatch.Elapsed.TotalMilliseconds);
                }
            }
            catch
            {
                // ignored
            }

            return (false, null);
        }

        public static (bool isReachable, double? httpPing) CheckProxyReachability(string scheme, string host, int port,
            string username, string password)
        {
            double? httpPing = null;

            try
            {
                var proxyUrl = new UriBuilder(scheme, host, port).Uri;
                using var handler = new HttpClientHandler();
                handler.Proxy = new WebProxy(proxyUrl)
                {
                    Credentials = !string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password)
                        ? new NetworkCredential(username, password)
                        : null
                };
                handler.UseProxy = true;

                using var client = new HttpClient(handler);
                client.Timeout = TimeSpan.FromMilliseconds(666);

                var task1 = client.GetAsync("http://www.gstatic.com/generate_204");
                var task2 = client.GetAsync("http://www.google.com/generate_204");

                var stopwatch = Stopwatch.StartNew();
                var completedTask = Task.WhenAny(task1, task2).Result;
                stopwatch.Stop();

                if (completedTask.Status == TaskStatus.RanToCompletion && completedTask.Result.IsSuccessStatusCode &&
                    completedTask.Result.StatusCode == HttpStatusCode.NoContent)
                {
                    httpPing = stopwatch.Elapsed.TotalMilliseconds;
                }
                else
                {
                    var otherTask = completedTask == task1 ? task2 : task1;
                    if (otherTask.Status == TaskStatus.RanToCompletion && otherTask.Result.IsSuccessStatusCode &&
                        otherTask.Result.StatusCode == HttpStatusCode.NoContent)
                    {
                        httpPing = stopwatch.Elapsed.TotalMilliseconds;
                    }
                }
            }
            catch
            {
                // ignored
            }

            return (httpPing.HasValue, httpPing);
        }

        public static string GenerateFixedCode(string input, string prefix, int length)
        {
            using var md5 = MD5.Create();
            var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(input));
            return prefix + BitConverter.ToString(hash).Replace("-", "").Substring(0, length).ToLower();
        }

        public static long Find(long x, Dictionary<long, long> parent)
        {
            if (parent[x] == x) return x;
            return parent[x] = Find(parent[x], parent);
        }

        public static void Union(long x, long y, Dictionary<long, long> parent)
        {
            var root1 = Find(x, parent);
            var root2 = Find(y, parent);
            if (root1 != root2) parent[root1] = root2;
        }

        public static bool IsDirectoryEmpty(string directoryPath)
        {
            if (!Directory.Exists(directoryPath))
                return false;

            foreach (var subdirectory in Directory.EnumerateDirectories(directoryPath))
                return false;

            foreach (var file in Directory.EnumerateFiles(directoryPath))
                return false;

            return true;
        }

        public class FileSystemMetadataComparer : IEqualityComparer<FileSystemMetadata>
        {
            public bool Equals(FileSystemMetadata x, FileSystemMetadata y)
            {
                if (x == null || y == null) return false;
                return x.FullName == y.FullName;
            }

            public int GetHashCode(FileSystemMetadata obj)
            {
                return obj.FullName.GetHashCode();
            }
        }
    }
}
