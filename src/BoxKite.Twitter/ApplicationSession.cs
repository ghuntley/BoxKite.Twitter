// (c) 2012-2014 Nick Hodge mailto:hodgenick@gmail.com & Brendan Forster
// License: MS-PL

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text;
using System.Threading.Tasks;
using BoxKite.Twitter.Authentication;
using BoxKite.Twitter.Extensions;
using BoxKite.Twitter.Models;
using Reactive.EventAggregator;

namespace BoxKite.Twitter
{
    public class ApplicationSession : IApplicationSession
    {
        private const string UserAgent = "BoxKite.Twitter/1.0";

        public IEventAggregator TwitterConnectionEvents { get; set; }
        public int WaitTimeoutSeconds { get; set; }
        public string clientID { get; set; }
        public string clientSecret { get; set; }
        public string bearerToken { get; set; }

        public ApplicationSession(string clientID, string clientSecret, int waitTimeoutSeconds = 30)
        {
            this.clientID = clientID;
            this.clientSecret = clientSecret;
            WaitTimeoutSeconds = waitTimeoutSeconds;
        }

        public ApplicationSession(string clientID, string clientSecret, string bearerToken, int waitTimeoutSeconds = 30)
        {
            this.clientID = clientID;
            this.clientSecret = clientSecret;
            this.bearerToken = bearerToken;
            WaitTimeoutSeconds = waitTimeoutSeconds;
        }

        /// <summary>
        /// Use OAuth2 Bearer To do read-only query
        /// </summary>
        /// <param name="url">URL to call</param>
        /// <param name="parameters">Params to send</param>
        /// <returns></returns>
        public async Task<HttpResponseMessage> GetAsync(string url, SortedDictionary<string, string> parameters)
        {
            if (clientID != null && clientSecret != null && bearerToken == null)
            {
                await this.StartApplicationOnlyAuth();
            }
 
            var querystring = parameters.Aggregate("", (current, entry) => current + (entry.Key + "=" + entry.Value + "&"));

            var oauth2 = String.Format("Bearer {0}",bearerToken);
            var fullUrl = url;

            var handler = new HttpClientHandler();
            if (handler.SupportsAutomaticDecompression)
            {
                handler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            }
            var client = new HttpClient(handler);
            client.DefaultRequestHeaders.Add("Authorization", oauth2);
            client.DefaultRequestHeaders.Add("User-Agent", UserAgent);

            if (!string.IsNullOrWhiteSpace(querystring))
                fullUrl += "?" + querystring.Substring(0, querystring.Length - 1);

            var download = client.GetAsync(fullUrl).ToObservable().Timeout(TimeSpan.FromSeconds(WaitTimeoutSeconds));
            var clientdownload = await download;

            return clientdownload;
        }

    }
}