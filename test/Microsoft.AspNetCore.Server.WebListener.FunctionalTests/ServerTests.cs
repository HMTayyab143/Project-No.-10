// Copyright (c) Microsoft Open Technologies, Inc.
// All Rights Reserved
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
// http://www.apache.org/licenses/LICENSE-2.0
//
// THIS CODE IS PROVIDED *AS IS* BASIS, WITHOUT WARRANTIES OR
// CONDITIONS OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING
// WITHOUT LIMITATION ANY IMPLIED WARRANTIES OR CONDITIONS OF
// TITLE, FITNESS FOR A PARTICULAR PURPOSE, MERCHANTABLITY OR
// NON-INFRINGEMENT.
// See the Apache 2 License for the specific language governing
// permissions and limitations under the License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Server;
using Xunit;

namespace Microsoft.AspNetCore.Server.WebListener
{
    public class ServerTests
    {
        [Fact]
        public async Task Server_200OK_Success()
        {
            string address;
            using (Utilities.CreateHttpServer(out address, httpContext =>
                {
                    return Task.FromResult(0);
                }))
            {
                string response = await SendRequestAsync(address);
                Assert.Equal(string.Empty, response);
            }
        }

        [Fact]
        public async Task Server_SendHelloWorld_Success()
        {
            string address;
            using (Utilities.CreateHttpServer(out address, httpContext =>
                {
                    httpContext.Response.ContentLength = 11;
                    return httpContext.Response.WriteAsync("Hello World");
                }))
            {
                string response = await SendRequestAsync(address);
                Assert.Equal("Hello World", response);
            }
        }

        [Fact]
        public async Task Server_EchoHelloWorld_Success()
        {
            string address;
            using (Utilities.CreateHttpServer(out address, httpContext =>
                {
                    string input = new StreamReader(httpContext.Request.Body).ReadToEnd();
                    Assert.Equal("Hello World", input);
                    httpContext.Response.ContentLength = 11;
                    return httpContext.Response.WriteAsync("Hello World");
                }))
            {
                string response = await SendRequestAsync(address, "Hello World");
                Assert.Equal("Hello World", response);
            }
        }

        [Fact]
        public async Task Server_ShutdownDurringRequest_Success()
        {
            Task<string> responseTask;
            ManualResetEvent received = new ManualResetEvent(false);
            string address;
            using (Utilities.CreateHttpServer(out address, httpContext =>
                {
                    received.Set();
                    httpContext.Response.ContentLength = 11;
                    return httpContext.Response.WriteAsync("Hello World");
                }))
            {
                responseTask = SendRequestAsync(address);
                Assert.True(received.WaitOne(10000));
            }
            string response = await responseTask;
            Assert.Equal("Hello World", response);
        }

        [Fact]
        public void Server_AppException_ClientReset()
        {
            string address;
            using (Utilities.CreateHttpServer(out address, httpContext =>
            {
                throw new InvalidOperationException();
            }))
            {
                Task<string> requestTask = SendRequestAsync(address);
                Assert.Throws<AggregateException>(() => requestTask.Result);

                // Do it again to make sure the server didn't crash
                requestTask = SendRequestAsync(address);
                Assert.Throws<AggregateException>(() => requestTask.Result);
            }
        }

        [Fact]
        public void Server_MultipleOutstandingSyncRequests_Success()
        {
            int requestLimit = 10;
            int requestCount = 0;
            TaskCompletionSource<object> tcs = new TaskCompletionSource<object>();

            string address;
            using (Utilities.CreateHttpServer(out address, httpContext =>
            {
                if (Interlocked.Increment(ref requestCount) == requestLimit)
                {
                    tcs.TrySetResult(null);
                }
                else
                {
                    tcs.Task.Wait();
                }

                return Task.FromResult(0);
            }))
            {
                List<Task> requestTasks = new List<Task>();
                for (int i = 0; i < requestLimit; i++)
                {
                    Task<string> requestTask = SendRequestAsync(address);
                    requestTasks.Add(requestTask);
                }

                Assert.True(Task.WaitAll(requestTasks.ToArray(), TimeSpan.FromSeconds(60)), "Timed out");
            }
        }

        [Fact]
        public void Server_MultipleOutstandingAsyncRequests_Success()
        {
            int requestLimit = 10;
            int requestCount = 0;
            TaskCompletionSource<object> tcs = new TaskCompletionSource<object>();

            string address;
            using (Utilities.CreateHttpServer(out address, async httpContext =>
            {
                if (Interlocked.Increment(ref requestCount) == requestLimit)
                {
                    tcs.TrySetResult(null);
                }
                else
                {
                    await tcs.Task;
                }
            }))
            {
                List<Task> requestTasks = new List<Task>();
                for (int i = 0; i < requestLimit; i++)
                {
                    Task<string> requestTask = SendRequestAsync(address);
                    requestTasks.Add(requestTask);
                }
                Assert.True(Task.WaitAll(requestTasks.ToArray(), TimeSpan.FromSeconds(60)), "Timed out");
            }
        }

        [Fact]
        public async Task Server_ClientDisconnects_CallCanceled()
        {
            TimeSpan interval = TimeSpan.FromSeconds(1);
            ManualResetEvent received = new ManualResetEvent(false);
            ManualResetEvent aborted = new ManualResetEvent(false);
            ManualResetEvent canceled = new ManualResetEvent(false);

            string address;
            using (Utilities.CreateHttpServer(out address, httpContext =>
            {
                CancellationToken ct = httpContext.RequestAborted;
                Assert.True(ct.CanBeCanceled, "CanBeCanceled");
                Assert.False(ct.IsCancellationRequested, "IsCancellationRequested");
                ct.Register(() => canceled.Set());
                received.Set();
                Assert.True(aborted.WaitOne(interval), "Aborted");
                Assert.True(ct.WaitHandle.WaitOne(interval), "CT Wait");
                Assert.True(ct.IsCancellationRequested, "IsCancellationRequested");
                return Task.FromResult(0);
            }))
            {
                // Note: System.Net.Sockets does not RST the connection by default, it just FINs.
                // Http.Sys's disconnect notice requires a RST.
                using (Socket socket = await SendHungRequestAsync("GET", address))
                {
                    Assert.True(received.WaitOne(interval), "Receive Timeout");

                    // Force a RST
                    socket.LingerState = new LingerOption(true, 0);
                    socket.Dispose();

                    aborted.Set();
                }
                Assert.True(canceled.WaitOne(interval), "canceled");
            }
        }

        [Fact]
        public async Task Server_Abort_CallCanceled()
        {
            TimeSpan interval = TimeSpan.FromSeconds(100);
            ManualResetEvent received = new ManualResetEvent(false);
            ManualResetEvent aborted = new ManualResetEvent(false);
            ManualResetEvent canceled = new ManualResetEvent(false);

            string address;
            using (Utilities.CreateHttpServer(out address, httpContext =>
            {
                CancellationToken ct = httpContext.RequestAborted;
                Assert.True(ct.CanBeCanceled, "CanBeCanceled");
                Assert.False(ct.IsCancellationRequested, "IsCancellationRequested");
                ct.Register(() => canceled.Set());
                received.Set();
                httpContext.Abort();
                Assert.True(canceled.WaitOne(interval), "Aborted");
                Assert.True(ct.IsCancellationRequested, "IsCancellationRequested");
                return Task.FromResult(0);
            }))
            {
                using (Socket socket = await SendHungRequestAsync("GET", address))
                {
                    Assert.True(received.WaitOne(interval), "Receive Timeout");
                    Assert.Throws<SocketException>(() => socket.Receive(new byte[10]));
                }
            }
        }

        [Fact]
        public async Task Server_SetQueueLimit_Success()
        {
            // This is just to get a dynamic port
            string address;
            using (Utilities.CreateHttpServer(out address, httpContext => Task.FromResult(0))) { }

            var factory = new ServerFactory(loggerFactory: null);
            var server = factory.CreateServer(configuration: null);
            var listener = server.Features.Get<Microsoft.Net.Http.Server.WebListener>();
            listener.UrlPrefixes.Add(UrlPrefix.Create(address));
            listener.SetRequestQueueLimit(1001);

            using (server)
            {
                server.Start(new DummyApplication(httpContext => Task.FromResult(0)));
                string response = await SendRequestAsync(address);
                Assert.Equal(string.Empty, response);
            }
        }

        private async Task<string> SendRequestAsync(string uri)
        {
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

        private async Task<Socket> SendHungRequestAsync(string method, string address)
        {
            // Connect with a socket
            Uri uri = new Uri(address);
            TcpClient client = new TcpClient();

            try
            {
                await client.ConnectAsync(uri.Host, uri.Port);
                NetworkStream stream = client.GetStream();

                // Send an HTTP GET request
                byte[] requestBytes = BuildGetRequest(method, uri);
                await stream.WriteAsync(requestBytes, 0, requestBytes.Length);

                // Return the opaque network stream
                return client.Client;
            }
            catch (Exception)
            {
                ((IDisposable)client).Dispose();
                throw;
            }
        }

        private byte[] BuildGetRequest(string method, Uri uri)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append(method);
            builder.Append(" ");
            builder.Append(uri.PathAndQuery);
            builder.Append(" HTTP/1.1");
            builder.AppendLine();

            builder.Append("Host: ");
            builder.Append(uri.Host);
            builder.Append(':');
            builder.Append(uri.Port);
            builder.AppendLine();

            builder.AppendLine();
            return Encoding.ASCII.GetBytes(builder.ToString());
        }
    }
}