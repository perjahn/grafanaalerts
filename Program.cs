using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace grafanaalerts
{
    class Alert
    {
        public JObject SimpleAlert { get; set; }
        public int Id { get; set; }
        public Task<JObject?> Task { get; set; }
        public JObject? FullAlert { get; set; }

        public Alert(JObject simpleAlert, int id, Task<JObject?> task)
        {
            SimpleAlert = simpleAlert;
            Id = id;
            Task = task;
            FullAlert = null;
        }
    }

    class TableRow
    {
        public string Key { get; set; } = string.Empty;
        public string[] Data { get; set; } = { };
    }

    class Program
    {
        static bool verbose = false;

        static async Task<int> Main(string[] args)
        {
            var parsedArgs = args.ToList();

            var cookie = ExtractArgumentValue(parsedArgs, "-c");
            verbose = ExtractArgumentFlag(parsedArgs, "-verbose");

            if (parsedArgs.Count != 2)
            {
                Log("Usage: grafanaalerts <grafanaurl> <authtoken> [-c cookie] [-verbose]", ConsoleColor.Red);
                return 1;
            }

            var url = parsedArgs[0];
            var authtoken = parsedArgs[1];
            var keyfieldname = "Name";

            int result = await ShowAlerts(url, authtoken, cookie, keyfieldname);

            return result;
        }

        static string? ExtractArgumentValue(List<string> args, string flagname)
        {
            int index = args.IndexOf(flagname);
            if (index < 0 || index >= args.Count - 1)
            {
                return null;
            }

            var values = args[index + 1];
            args.RemoveRange(index, 2);
            return values;
        }

        static bool ExtractArgumentFlag(List<string> args, string flagname)
        {
            int index = args.IndexOf(flagname);
            if (index < 0)
            {
                return false;
            }

            args.RemoveAt(index);
            return true;
        }

        static async Task<int> ShowAlerts(string url, string authtoken, string? cookie, string keyfieldname)
        {
            string baseaddress = GetBaseAddress(url);
            if (baseaddress == string.Empty)
            {
                Log($"Invalid url: '{url}'", ConsoleColor.Red);
                return 1;
            }

            var handler = new HttpClientHandler { UseCookies = false };
            var client = new HttpClient(handler);

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", authtoken);
            client.BaseAddress = new Uri(baseaddress);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));


            JArray simpleAlerts = await GetAlerts(client, cookie);

            Log($"Found {simpleAlerts.Count} alerts in grafana.", ConsoleColor.Green);

            var alertList = new List<Alert>();

            foreach (var simpleAlert in simpleAlerts)
            {
                if (simpleAlert is JObject jobject && jobject["id"] is JValue jvalue && jvalue?.Value<string>() != null && int.TryParse(jvalue?.Value<string>(), out int id))
                {
                    alertList.Add(new Alert(jobject, id, GetAlert(client, id, cookie)));
                }
                else
                {
                    Log($"Ignoring invalid alert: '{simpleAlert.ToString()}'", ConsoleColor.Yellow);
                }
            }

            Log($"Found {alertList.Count} valid alerts in grafana.", ConsoleColor.Green);

            if (alertList.Count == 0)
            {
                return 0;
            }

            for (int i = 0; i < alertList.Count;)
            {
                var item = alertList[i];
                var alert = await item.Task;
                if (alert != null)
                {
                    alertList[i].FullAlert = alert;
                    i++;
                }
                else
                {
                    Log($"Ignoring invalid alert value: '{item.SimpleAlert.ToString()}'", ConsoleColor.Yellow);
                    alertList.RemoveAt(i);
                }
            }

            Log($"Got {alertList.Count} valid alert values from grafana.", ConsoleColor.Green);

            if (alertList.Count == 0)
            {
                return 0;
            }



            var allHeaders = new List<string>();

            foreach (var item in alertList)
            {
                var alert = item.FullAlert;

                if (alert != null)
                {
                    foreach (var token in alert.Children())
                    {
                        if (token is JProperty jproperty)
                        {
                            var name = jproperty.Name;
                            if (!allHeaders.Contains(name))
                            {
                                allHeaders.Add(name);
                            }
                        }
                    }
                }
                else
                {
                    Log($"Error: Alert: '{item.SimpleAlert.ToString()}'");
                }
            }
            allHeaders.Add("dashboardUid");


            var headerRow = new TableRow();
            headerRow.Key = "Alerts";
            headerRow.Data = allHeaders.Where(h => h != keyfieldname).OrderBy(h => h).ToArray();

            var allAlerts = new List<TableRow>();
            allAlerts.Add(headerRow);

            int rowcount = 0;
            foreach (var item in alertList)
            {
                if (item.FullAlert == null)
                {
                    continue;
                }
                JObject alert = item.FullAlert;
                var keyValue = alert[keyfieldname];
                string? key;
                if (keyValue == null || (key = keyValue.Value<string>()) == null)
                {
                    Log($"Ignoring invalid alert, missing '{keyfieldname}' field: '{alert.ToString()}'");
                    continue;
                }

                var row = new TableRow();
                row.Key = key;

                var values = new List<string>();

                foreach (var fieldname in headerRow.Data)
                {
                    var value = alert[fieldname];
                    if (value == null)
                    {
                        values.Add(string.Empty);
                    }
                    else
                    {
                        var s = value is JObject ? value.ToString() : value.Value<string>();
                        s = s ?? string.Empty;
                        s = s.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Replace("  ", " ").Replace("  ", " ").Replace("  ", " ");

                        values.Add(GetFirst(s, 9));
                    }
                }

                values[2] = item.SimpleAlert["dashboardUid"]?.Value<string>() ?? string.Empty;

                row.Data = values.ToArray();

                allAlerts.Add(row);
                rowcount++;
            }

            Log($"Valid alerts count: {allAlerts.Count - 1}");

            ShowTable(allAlerts.ToArray());

            await Task.CompletedTask;

            return 0;
        }

        static async Task<JObject?> GetAlert(HttpClient client, int id, string? cookie)
        {
            string url = $"api/alerts/{id}";
            var request = new HttpRequestMessage(HttpMethod.Get, url);

            if (cookie != null)
            {
                request.Headers.Add("Cookie", cookie);
            }

            LogVerbose($"Request {client.BaseAddress}{url}");

            using var response = await client.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                Log($"Url: '{url}', Response: '{json}'", ConsoleColor.Red);
            }
            response.EnsureSuccessStatusCode();

            if (!TryParseJObject(json, out JObject jobject))
            {
                File.WriteAllText("error.html", json);
                Log($"Couldn't parse json (saved to error.html): {GetFirstCharacters(json, 100)}", ConsoleColor.Yellow);
                //throw new Exception($"Couldn't parse json (saved to error.html): {GetFirstCharacters(json, 100)}");
                return null;
            }

            LogVerbose($"Response: >>>{jobject.ToString()}<<<");

            return jobject;
        }

        static void ShowTable(TableRow[] rows)
        {
            if (rows.Length == 0)
            {
                return;
            }

            foreach (var row in rows)
            {
                for (int col = 0; col < row.Data.Length; col++)
                {
                    if (row.Data[col].Length > 1)
                    {
                        row.Data[col] = row.Data[col];
                    }
                }
            }


            string separator = new string(' ', 2);
            int[] maxwidths = GetMaxWidths(rows);

            bool headerRow = true;

            foreach (var row in rows)
            {
                var output = new StringBuilder();

                output.AppendFormat("{0,-" + maxwidths[0] + "}", row.Key);

                for (int col = 0; col < row.Data.Length; col++)
                {
                    output.AppendFormat("{0}{1,-" + maxwidths[col + 1] + "}", separator, row.Data[col]);
                }

                string textrow = output.ToString().TrimEnd();

                if (headerRow)
                {
                    Log(textrow);
                    headerRow = false;
                }
                else
                {
                    if (textrow.Contains('*'))
                    {
                        Log(textrow);
                    }
                    else
                    {
                        Log(textrow, ConsoleColor.Yellow);
                    }
                }
            }
        }

        static string GetFirst(string text, int chars)
        {
            return text.Length < chars ? text : text.Substring(0, chars - 3) + "...";
        }

        static int Compare(string[] arr1, string[] arr2)
        {
            foreach (var element in arr1.Zip(arr2))
            {
                int result = string.Compare(element.First, element.Second, StringComparison.OrdinalIgnoreCase);
                if (result < 0)
                {
                    return -1;
                }
                if (result > 0)
                {
                    return 1;
                }
            }
            if (arr1.Length < arr2.Length)
            {
                return -1;
            }
            if (arr1.Length > arr2.Length)
            {
                return 1;
            }

            return 0;
        }

        static int[] GetMaxWidths(TableRow[] rows)
        {
            if (rows.Length == 0)
            {
                return Array.Empty<int>();
            }

            int[] maxwidths = new int[rows[0].Data.Length + 1];

            for (var row = 0; row < rows.Length; row++)
            {
                if (row == 0)
                {
                    maxwidths[0] = rows[row].Key.Length;
                }
                else
                {
                    if (rows[row].Key.Length > maxwidths[0])
                    {
                        maxwidths[0] = rows[row].Key.Length;
                    }
                }
                for (var col = 0; col < rows[0].Data.Length && col < rows[row].Data.Length; col++)
                {
                    if (row == 0)
                    {
                        maxwidths[col + 1] = rows[row].Data[col].Length;
                    }
                    else
                    {
                        if (rows[row].Data[col].Length > maxwidths[col + 1])
                        {
                            maxwidths[col + 1] = rows[row].Data[col].Length;
                        }
                    }
                }
            }

            return maxwidths;
        }

        static async Task<JArray> GetAlerts(HttpClient client, string? cookie)
        {
            string url = "api/alerts";
            var request = new HttpRequestMessage(HttpMethod.Get, url);

            if (cookie != null)
            {
                request.Headers.Add("Cookie", cookie);
            }

            LogVerbose($"Request {client.BaseAddress}{url}");

            using var response = await client.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                Log(json, ConsoleColor.Red);
            }
            response.EnsureSuccessStatusCode();

            if (!TryParseJArray(json, out JArray jarray))
            {
                File.WriteAllText("error.html", json);
                throw new Exception($"Couldn't parse json (saved to error.html): {GetFirstCharacters(json, 100)}");
            }

            LogVerbose($"Response: >>>{jarray.ToString()}<<<");

            return jarray;
        }

        static string GetBaseAddress(string url)
        {
            int end;
            if (url.StartsWith("https://"))
            {
                end = url.IndexOf("/", 8);
            }
            else if (url.StartsWith("http://"))
            {
                end = url.IndexOf("/", 7);
            }
            else
            {
                return string.Empty;
            }

            if (end < 0)
            {
                return url;
            }
            return url.Substring(0, end);
        }

        static string GetFirstCharacters(string text, int characters)
        {
            if (characters < text.Length)
            {
                return text.Substring(0, characters) + "...";
            }
            else
            {
                return text.Substring(0, text.Length);
            }
        }

        static bool TryParseJObject(string json, out JObject jobject)
        {
            try
            {
                jobject = JObject.Parse(json);
                return true;
            }
            catch
            {
                jobject = new JObject();
                return false;
            }
        }

        static bool TryParseJArray(string json, out JArray jarray)
        {
            try
            {
                JsonReader reader = new JsonTextReader(new StringReader(json));
                reader.DateParseHandling = DateParseHandling.None;
                jarray = JArray.Load(reader);

                //jarray = JArray.Parse(json);
                return true;
            }
            catch
            {
                jarray = new JArray();
                return false;
            }
        }

        static void Log(string message)
        {
            Console.WriteLine(message);
        }

        static void Log(string message, ConsoleColor color)
        {
            var oldcolor = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine(message);
            Console.ForegroundColor = oldcolor;
        }

        static void LogVerbose(string message)
        {
            if (verbose)
            {
                var oldcolor = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine(message);
                Console.ForegroundColor = oldcolor;
            }
        }
    }
}
