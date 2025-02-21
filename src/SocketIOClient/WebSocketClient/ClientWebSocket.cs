﻿using SocketIOClient.Packgers;
using System;
using System.Diagnostics;
using System.Linq;
using System.Net.WebSockets;
using System.Threading.Tasks;
using System.Threading;
using System.Text;
using System.Collections.Generic;
using SocketIOClient.Exceptions;

namespace SocketIOClient.WebSocketClient
{
    /// <summary>
    /// Internally uses 'System.Net.WebSockets.ClientWebSocket' as websocket client
    /// </summary>
    public sealed class ClientWebSocket : IWebSocketClient
    {
        public ClientWebSocket(SocketIO io, PackgeManager parser)
        {
            _parser = parser;
            _io = io;
        }

        const int ReceiveChunkSize = 1024;
        const int SendChunkSize = 1024;

        readonly PackgeManager _parser;
        readonly SocketIO _io;
        System.Net.WebSockets.ClientWebSocket _ws;
        CancellationTokenSource _wsWorkTokenSource;

        public Action<ClientWebSocketOptions> Config { get; set; }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="uri"></param>
        /// <param name="options"></param>
        /// <exception cref="TimeoutException"></exception>
        /// <exception cref="WebSocketException"></exception>
        /// <returns></returns>
        public async Task ConnectAsync(Uri uri)
        {
            if (_ws != null)
                _ws.Dispose();
            _ws = new System.Net.WebSockets.ClientWebSocket();
            Config?.Invoke(_ws.Options);

            _wsWorkTokenSource = new CancellationTokenSource();
            var wsConnectionTokenSource = new CancellationTokenSource(_io.Options.ConnectionTimeout);
            try
            {
                await _ws.ConnectAsync(uri, wsConnectionTokenSource.Token);
                _ = Task.Run(ListenAsync);
            }
            catch (TaskCanceledException)
            {
                throw new TimeoutException();
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="text"></param>
        /// <returns></returns>
        /// <exception cref="InvalidSocketStateException"></exception>
        public async Task SendMessageAsync(string text)
        {
            if (_ws == null)
            {
                throw new InvalidSocketStateException("Faild to emit, websocket is not connected yet.");
            }
            if (_ws.State != WebSocketState.Open)
            {
                throw new InvalidSocketStateException("Connection is not open.");
            }

            var messageBuffer = Encoding.UTF8.GetBytes(text);
            var messagesCount = (int)Math.Ceiling((double)messageBuffer.Length / SendChunkSize);

            for (var i = 0; i < messagesCount; i++)
            {
                var offset = (SendChunkSize * i);
                var count = SendChunkSize;
                var lastMessage = ((i + 1) == messagesCount);

                if ((count * (i + 1)) > messageBuffer.Length)
                {
                    count = messageBuffer.Length - offset;
                }

                await _ws.SendAsync(new ArraySegment<byte>(messageBuffer, offset, count), WebSocketMessageType.Text, lastMessage, _wsWorkTokenSource.Token);
            }
#if DEBUG
            Trace.WriteLine($"⬆ {DateTime.Now} {text}");
#endif
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="bytes"></param>
        /// <returns></returns>
        /// <exception cref="InvalidSocketStateException"></exception>
        public async Task SendMessageAsync(byte[] bytes)
        {
            if (_ws == null)
            {
                throw new InvalidSocketStateException("Faild to emit, websocket is not connected yet.");
            }
            if (_ws.State != WebSocketState.Open)
            {
                throw new InvalidSocketStateException("Connection is not open.");
            }
            var messagesCount = (int)Math.Ceiling((double)bytes.Length / SendChunkSize);
            for (var i = 0; i < messagesCount; i++)
            {
                var offset = (SendChunkSize * i);
                var count = SendChunkSize;
                var lastMessage = ((i + 1) == messagesCount);

                if ((count * (i + 1)) > bytes.Length)
                {
                    count = bytes.Length - offset;
                }

                await _ws.SendAsync(new ArraySegment<byte>(bytes, offset, count), WebSocketMessageType.Binary, lastMessage, _wsWorkTokenSource.Token);
            }
#if DEBUG
            Trace.WriteLine($"⬆ {DateTime.Now} Binary message");
#endif
        }

        public async Task DisconnectAsync()
        {
            await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
            Close(null);
        }

        private async Task ListenAsync()
        {
            while (true)
            {
                var buffer = new byte[ReceiveChunkSize];
                var stringResult = new List<byte>();
                var binaryResult = new List<byte>();
                WebSocketReceiveResult result = null;
                while (_ws.State == WebSocketState.Open)
                {
                    try
                    {
                        //result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _wsWorkTokenSource.Token);
                        result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            Close("io server disconnect");
                            break;
                        }
                        else if (result.MessageType == WebSocketMessageType.Text)
                        {
                            stringResult.AddRange(buffer.Take(result.Count));
                        }
                        else if (result.MessageType == WebSocketMessageType.Binary)
                        {
                            binaryResult.AddRange(buffer.Take(result.Count));
                        }
                        if (result.EndOfMessage)
                        {
                            break;
                        }
                    }
                    catch (WebSocketException e)
                    {
                        Close(e.Message);
                        break;
                    }
                }
                if (result == null)
                {
                    break;
                }
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    string message = Encoding.UTF8.GetString(stringResult.ToArray());
#if DEBUG
                    Trace.WriteLine($"⬇ {DateTime.Now} {message}");
#endif
                    _parser.Unpack(message);
                }
                else if (result.MessageType == WebSocketMessageType.Binary)
                {
#if DEBUG
                    Trace.WriteLine($"⬇ {DateTime.Now} Binary message");
#endif
                    _io.InvokeBytesReceived(_io.Options.EIO == 4 ? binaryResult.ToArray() : binaryResult.Skip(1).ToArray());
                }
            }
        }

        private void Close(string reason)
        {
            if (reason != null)
            {
                _io.InvokeDisconnect(reason);
            }
        }
    }
}
