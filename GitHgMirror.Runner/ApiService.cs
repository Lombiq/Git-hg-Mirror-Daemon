using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using GitHgMirror.CommonTypes;
using Newtonsoft.Json;

namespace GitHgMirror.Runner
{
    class ApiService
    {
        private readonly Settings _settings;


        public ApiService(Settings settings)
        {
            _settings = settings;
        }


        public T Get<T>(string path)
        {
            T value = default(T);
            PrepareWebClientCall(path, (url, wc) =>
                {
                    value = JsonConvert.DeserializeObject<T>(wc.DownloadString(url));
                });
            return value;
        }

        public void Post(string path, object value)
        {
            PrepareWebClientCall(path, (url, wc) =>
                {
                    wc.UploadData(url, "POST", Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value)));
                });
        }


        private void PrepareWebClientCall(string path, Action<string, WebClient> execute)
        {
            using (var wc = new WebClient())
            {
                // Setting UTF-8 is needed for accented characters to work properly.
                wc.Headers.Add(HttpRequestHeader.ContentType, "application/json; charset=utf-8");
                var apiUrl = _settings.ApiEndpointUrl.ToString();
                var url = apiUrl + (!apiUrl.EndsWith("/") ? "/" : "") + path.Trim('/');
                url += (url.Contains('?') ? "&" : "?") + "password=" + _settings.ApiPassword;
                try
                {
                    execute(url, wc);
                }
                catch (WebException ex)
                {
                    throw new WebException("The web operation for the url " + url + " failed with the following erorr: " + ex.Message, ex);
                }
            }
        }
    }
}
