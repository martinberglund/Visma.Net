﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using ONIT.VismaNetApi.Models;

namespace ONIT.VismaNetApi.Lib
{
    internal class VismaNetHttpClient : IDisposable
    {
        private static readonly JsonSerializerSettings _serializerSettings = new JsonSerializerSettings
        {
            DateFormatString = "yyyy-MM-dd HH:mm:ss",
            Converters =
            {
                new StringEnumConverter()
            }
        };

        private static readonly HttpClient httpClient;

        private readonly VismaNetAuthorization authorization;

        static VismaNetHttpClient()
        {
            var handler = new HttpClientHandler();
            if (handler.SupportsAutomaticDecompression)
                handler.AutomaticDecompression = DecompressionMethods.GZip |
                                                 DecompressionMethods.Deflate;
            handler.UseCookies = false;
            httpClient = new HttpClient(handler, true);
            httpClient.Timeout = TimeSpan.FromSeconds(300);
            httpClient.DefaultRequestHeaders.Add("User-Agent",
                $"Visma.Net/{VismaNet.Version} (+https://github.com/ON-IT/Visma.Net)");
            httpClient.DefaultRequestHeaders.ExpectContinue = false;
        }

        internal VismaNetHttpClient(VismaNetAuthorization auth = null)
        {
            authorization = auth;
        }

        #region IDisposable implementation

        public void Dispose()
        {
            // http://stackoverflow.com/questions/11178220/is-httpclient-safe-to-use-concurrently
            //if (httpClient != null)
            //    httpClient.Dispose();
            GC.SuppressFinalize(this);
        }

        #endregion

        internal HttpRequestMessage PrepareMessage(HttpMethod method, string resource)
        {
            var message = new HttpRequestMessage(method, resource);
            if (authorization != null)
            {
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authorization.Token);
                message.Headers.Add("ipp-company-id", string.Format("{0}", authorization.CompanyId));
                if (authorization.BranchId > 0)
                    message.Headers.Add("branchid", authorization.BranchId.ToString());
            }
            message.Headers.Add("ipp-application-type", VismaNetApiHelper.ApplicationType);
            message.Headers.Accept.Clear();
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (!string.IsNullOrEmpty(VismaNet.ApplicationName))
                message.Headers.Add("User-Agent",
                    $"Visma.Net/{VismaNet.Version} (+https://github.com/ON-IT/Visma.Net) ({VismaNet.ApplicationName})");
            return message;
        }


        internal async Task ForEachInStream<T>(string url, Func<T, Task> action) where T : DtoProviderBase
        {
            using (var result = await httpClient.SendAsync(PrepareMessage(HttpMethod.Get, url),
                HttpCompletionOption.ResponseHeadersRead))
            using (var stream = await result.Content.ReadAsStreamAsync())
            using (var reader = new StreamReader(stream))
            {
                foreach (var element in DeserializeSequenceFromJson<T>(reader))
                {
                    element.PrepareForUpdate();
                    await action(element);
                }
            }
        }

        internal async Task<T> Get<T>(string url)
        {
            url = url.Replace("http://", "https://"); // force https
            var result = await httpClient.SendAsync(PrepareMessage(HttpMethod.Get, url));
            var stringData = await result.Content.ReadAsStringAsync();
            if (result.StatusCode != HttpStatusCode.OK)
            {
                VismaNetExceptionHandler.HandleException(stringData, null, null, url);
                return default(T);
            }
            if (string.IsNullOrEmpty(stringData))
                return default(T);

			return await Deserialize<T>(stringData);
        }

		internal async Task<Stream> GetStream(string url)
		{
			url = url.Replace("http://", "https://"); // force https
			var result = await httpClient.SendAsync(PrepareMessage(HttpMethod.Get, url));
			var streamData = await result.Content.ReadAsStreamAsync();
			if (result.StatusCode != HttpStatusCode.OK)
			{
                VismaNetExceptionHandler.HandleException("Error downloading stream from Visma.net", null, null, url);
			}
			return streamData;

		}

		internal async Task<T> PostMessage<T>(string url, HttpContent httpContent) where T : class
        {
            var message = PrepareMessage(HttpMethod.Post, url);
            using (message.Content = httpContent)
            {
                var result = await httpClient.SendAsync(message);
                if (!result.IsSuccessStatusCode)
                    VismaNetExceptionHandler.HandleException(await result.Content.ReadAsStringAsync(), null, await httpContent.ReadAsStringAsync(), url);
                if (result.Headers.Location != null)
                    if (typeof(T) == typeof(string))
                    {
                        var absoluteUri = result.Headers.Location.AbsoluteUri;
                        return absoluteUri.Substring(absoluteUri.LastIndexOf("/") + 1) as T;
                    }

                var content = await result.Content.ReadAsStringAsync();
                if (!string.IsNullOrEmpty(content))
                    return JsonConvert.DeserializeObject<T>(content);

                return default(T);
            }
        }

        internal async Task<T> Post<T>(string url, object data, string urlToGet=null)
        {
            var message = PrepareMessage(HttpMethod.Post, url);
            var serialized = await Serialize(data);
            using (message.Content = new StringContent(serialized, Encoding.UTF8, "application/json"))
            {
                var result = await httpClient.SendAsync(message);

                if (result.Headers.Location != null)
                {
                    // Fix for Visma not returning correct URL when createing salesorders of not SO type
                    if (urlToGet == null)
                    {
                        return await Get<T>(result.Headers.Location.AbsoluteUri);
                    }
                    else
                    {
                        string pattern = @".(.*)\/(\d+)";
                        string substitution = @"$2";
                        var regex = new System.Text.RegularExpressions.Regex(pattern);
                        var id = regex.Replace(result.Headers.Location.AbsoluteUri, substitution);
                        return await Get<T>($"{urlToGet}/{id}");
                    }
                }
                if (result.StatusCode == HttpStatusCode.NoContent)
                    return await Get<T>(url);

                var stringData = await result.Content.ReadAsStringAsync();

                if (result.StatusCode != HttpStatusCode.OK)
                {
                    VismaNetExceptionHandler.HandleException(stringData, null, serialized);
                    return default(T);
                }
                if (string.IsNullOrEmpty(stringData))
                    return default(T);
                try
                {
                    return await Deserialize<T>(stringData);
                }
                catch (Exception)
                {
                    throw new Exception("Could not serialize:" + Environment.NewLine + Environment.NewLine +
                                        stringData);
                }
            }
        }

        internal async Task<T> Put<T>(string url, object data, string urlToGet=null)
        {
            var message = PrepareMessage(HttpMethod.Put, url);
            var serialized = await Serialize(data);
            using (var content = new StringContent(serialized, Encoding.UTF8, "application/json"))
            {
                message.Content = content;
                var result = await httpClient.SendAsync(message);
                if (result.Headers.Location != null)
                    return await Get<T>(result.Headers.Location.AbsoluteUri);
                if (result.StatusCode == HttpStatusCode.NoContent)
                    if (urlToGet != null) 
                        return await Get<T>(urlToGet);
                    else
                        return await Get<T>(url);
                var stringData = await result.Content.ReadAsStringAsync();
                if (result.StatusCode != HttpStatusCode.OK)
                {
                    VismaNetExceptionHandler.HandleException(stringData, null, serialized, url);
                    return default(T);
                }

                if (string.IsNullOrEmpty(stringData))
                    return default(T);
                return await Deserialize<T>(stringData);
            }
        }

        private async Task<string> Serialize(object obj)
        {
            return
                await
                    Task.Factory.StartNew(() => JsonConvert.SerializeObject(obj, Formatting.Indented, _serializerSettings));
        }

        private async Task<T> Deserialize<T>(string str)
        {
            return await Task.Factory.StartNew(() => JsonConvert.DeserializeObject<T>(str));
        }

        // http://stackoverflow.com/a/24115672/491094
        internal static IEnumerable<T> DeserializeSequenceFromJson<T>(TextReader readerStream)
        {
            using (var reader = new JsonTextReader(readerStream))
            {
                var serializer = new JsonSerializer();
                if (!reader.Read() || reader.TokenType != JsonToken.StartArray)
                    throw new Exception("Expected start of array in the deserialized json string");

                while (reader.Read())
                {
                    if (reader.TokenType == JsonToken.EndArray) break;
                    var item = serializer.Deserialize<T>(reader);
                    yield return item;
                }
            }
        }
    }
}