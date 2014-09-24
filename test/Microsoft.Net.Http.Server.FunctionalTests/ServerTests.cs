﻿// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.txt in the project root for license information.

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Microsoft.Net.Http.Server
{
    public class ServerTests
    {
        [Fact]
        public async Task Server_200OK_Success()
        {
            string address;
            using (var server = Utilities.CreateHttpServer(out address))
            {
                Task<string> responseTask = SendRequestAsync(address);

                var context = await server.GetContextAsync();
                context.Dispose();

                string response = await responseTask;
                Assert.Equal(string.Empty, response);
            }
        }

        [Fact]
        public async Task Server_SendHelloWorld_Success()
        {
            string address;
            using (var server = Utilities.CreateHttpServer(out address))
            {
                Task<string> responseTask = SendRequestAsync(address);
                
                var context = await server.GetContextAsync();
                context.Response.ContentLength = 11;
                using (var writer = new StreamWriter(context.Response.Body))
                {
                    writer.Write("Hello World");
                }

                string response = await responseTask;
                Assert.Equal("Hello World", response);
            }
        }

        [Fact]
        public async Task Server_EchoHelloWorld_Success()
        {
            string address;
            using (var server = Utilities.CreateHttpServer(out address))
            {
                Task<string> responseTask = SendRequestAsync(address, "Hello World");

                var context = await server.GetContextAsync();
                string input = new StreamReader(context.Request.Body).ReadToEnd();
                Assert.Equal("Hello World", input);
                context.Response.ContentLength = 11;
                using (var writer = new StreamWriter(context.Response.Body))
                {
                    writer.Write("Hello World");
                }

                string response = await responseTask;
                Assert.Equal("Hello World", response);
            }
        }

        [Fact]
        public async Task Server_ClientDisconnects_CallCanceled()
        {
            TimeSpan interval = TimeSpan.FromSeconds(1);
            ManualResetEvent canceled = new ManualResetEvent(false);

            string address;
            using (var server = Utilities.CreateHttpServer(out address))
            {
                using (var client = new HttpClient())
                {
                    Task<HttpResponseMessage> responseTask = client.GetAsync(address);

                    var context = await server.GetContextAsync();
                    CancellationToken ct = context.DisconnectToken;
                    Assert.True(ct.CanBeCanceled, "CanBeCanceled");
                    Assert.False(ct.IsCancellationRequested, "IsCancellationRequested");
                    ct.Register(() => canceled.Set());

                    client.CancelPendingRequests();

                    Assert.True(canceled.WaitOne(interval), "canceled");
                    Assert.True(ct.IsCancellationRequested, "IsCancellationRequested");

                    await Assert.ThrowsAsync<TaskCanceledException>(() => responseTask);

                    context.Dispose();
                }
            }
        }

        [Fact]
        public async Task Server_Abort_CallCanceled()
        {
            TimeSpan interval = TimeSpan.FromSeconds(1);
            ManualResetEvent canceled = new ManualResetEvent(false);

            string address;
            using (var server = Utilities.CreateHttpServer(out address))
            {
                var responseTask = SendRequestAsync(address);

                var context = await server.GetContextAsync();
                CancellationToken ct = context.DisconnectToken;
                Assert.True(ct.CanBeCanceled, "CanBeCanceled");
                Assert.False(ct.IsCancellationRequested, "IsCancellationRequested");
                ct.Register(() => canceled.Set());
                context.Abort();
                Assert.True(canceled.WaitOne(interval), "Aborted");
                Assert.True(ct.IsCancellationRequested, "IsCancellationRequested");

                // HttpClient re-tries the request because it doesn't know if the request was received.
                context = await server.GetContextAsync();
                context.Abort();

                await Assert.ThrowsAsync<HttpRequestException>(() => responseTask);
            }
        }

        [Fact]
        public async Task Server_ConnectionCloseHeader_CancellationTokenFires()
        {
            TimeSpan interval = TimeSpan.FromSeconds(1);
            ManualResetEvent canceled = new ManualResetEvent(false);

            string address;
            using (var server = Utilities.CreateHttpServer(out address))
            {
                Task<string> responseTask = SendRequestAsync(address);

                var context = await server.GetContextAsync();
                CancellationToken ct = context.DisconnectToken;
                Assert.True(ct.CanBeCanceled, "CanBeCanceled");
                Assert.False(ct.IsCancellationRequested, "IsCancellationRequested");
                ct.Register(() => canceled.Set());

                context.Response.Headers["Connection"] = "close";

                context.Response.ContentLength = 11;
                using (var writer = new StreamWriter(context.Response.Body))
                {
                    writer.Write("Hello World");
                }

                Assert.True(canceled.WaitOne(interval), "Disconnected");
                Assert.True(ct.IsCancellationRequested, "IsCancellationRequested");

                string response = await responseTask;
                Assert.Equal("Hello World", response);
            }
        }

        [Fact]
        public async Task Server_SetQueueLimit_Success()
        {
            string address;
            using (var server = Utilities.CreateHttpServer(out address))
            {
                server.SetRequestQueueLimit(1001);
                Task<string> responseTask = SendRequestAsync(address);

                var context = await server.GetContextAsync();
                context.Dispose();

                string response = await responseTask;
                Assert.Equal(string.Empty, response);
            }
        }

        private async Task<string> SendRequestAsync(string uri)
        {
            ServicePointManager.DefaultConnectionLimit = 100;
            using (HttpClient client = new HttpClient())
            {
                return await client.GetStringAsync(uri);
            }
        }

        private async Task<string> SendRequestAsync(string uri, string upload)
        {
            using (HttpClient client = new HttpClient())
            {
                HttpResponseMessage response = await client.PostAsync(uri, new StringContent(upload));
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync();
            }
        }
    }
}