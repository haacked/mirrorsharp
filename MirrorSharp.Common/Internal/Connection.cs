﻿using System;
using System.Collections.Immutable;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using MirrorSharp.Internal.Commands;
using Newtonsoft.Json;

namespace MirrorSharp.Internal {
    public class Connection : ICommandResultSender, IDisposable {
        private readonly WebSocket _socket;
        private readonly WorkSession _session;
        private readonly ImmutableArray<ICommandHandler> _handlers;
        private readonly byte[] _inputByteBuffer = new byte[2048];
        private readonly byte[] _outputByteBuffer = new byte[4*1024];

        private readonly MemoryStream _jsonOutputStream;
        private readonly JsonWriter _jsonWriter;
        private readonly IConnectionOptions _options;

        public Connection(WebSocket socket, WorkSession session, ImmutableArray<ICommandHandler> handlers, IConnectionOptions options = null) {
            _socket = socket;
            _session = session;
            _handlers = handlers;
            _jsonOutputStream = new MemoryStream(_outputByteBuffer);
            _jsonWriter = new JsonTextWriter(new StreamWriter(_jsonOutputStream));
            _options = options ?? new MirrorSharpOptions();
        }

        public bool IsConnected => _socket.State == WebSocketState.Open;

        public async Task ReceiveAndProcessAsync(CancellationToken cancellationToken) {
            try {
                await ReceiveAndProcessInternalAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) {
                try {
                    await SendErrorAsync(ex.Message, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception sendException) {
                    throw new AggregateException(ex, sendException);
                }
                throw;
            }
        }

        private async Task ReceiveAndProcessInternalAsync(CancellationToken cancellationToken) {
            var received = await _socket.ReceiveAsync(new ArraySegment<byte>(_inputByteBuffer), cancellationToken).ConfigureAwait(false);
            if (received.MessageType == WebSocketMessageType.Binary)
                throw new FormatException("Expected text data (received binary).");

            if (received.MessageType == WebSocketMessageType.Close) {
                await _socket.CloseAsync(received.CloseStatus ?? WebSocketCloseStatus.Empty, received.CloseStatusDescription, cancellationToken).ConfigureAwait(false);
                return;
            }

            var data = new ArraySegment<byte>(_inputByteBuffer, 0, received.Count);
            var commandId = data.Array[data.Offset];
            var handler = ResolveHandler(commandId);
            await handler.ExecuteAsync(Shift(data), _session, this, cancellationToken).ConfigureAwait(false);
            if (_options.SendDebugCompareMessages)
                await SendDebugCompareAsync(handler, commandId, cancellationToken).ConfigureAwait(false);
        }

        private ICommandHandler ResolveHandler(byte commandId) {
            var handlerIndex = commandId - (byte)'A';
            if (handlerIndex < 0 || handlerIndex > _handlers.Length - 1)
                throw new FormatException($"Invalid command: '{(char)commandId}'.");

            var handler = _handlers[handlerIndex];
            if (handler == null)
                throw new FormatException($"Unknown command: '{(char)commandId}'.");
            return handler;
        }

        private ArraySegment<byte> Shift(ArraySegment<byte> data) {
            return new ArraySegment<byte>(data.Array, data.Offset + 1, data.Count - 1);
        }

        private Task SendDebugCompareAsync(ICommandHandler handler, byte commandId, CancellationToken cancellationToken) {
            if (!handler.CanChangeSession)
                return TaskEx.CompletedTask;

            // TODO: Replace with something better
            if (commandId == 'P') // let's wait for last one
                return TaskEx.CompletedTask;

            var writer = StartJsonMessage("debug:compare");
            if (!(handler is MoveCursorHandler))
                writer.WriteProperty("text", _session.SourceText.ToString());
            writer.WriteProperty("cursor", _session.CursorPosition);
            writer.WriteProperty("completion", _session.CurrentCompletionList != null);
            return SendJsonMessageAsync(cancellationToken);
        }

        private Task SendErrorAsync(string message, CancellationToken cancellationToken) {
            var writer = StartJsonMessage("error");
            writer.WriteProperty("message", message);
            return SendJsonMessageAsync(cancellationToken);
        }

        private JsonWriter StartJsonMessage(string messageTypeName) {
            _jsonOutputStream.Seek(0, SeekOrigin.Begin);
            _jsonWriter.WriteStartObject();
            _jsonWriter.WriteProperty("type", messageTypeName);
            return _jsonWriter;
        }

        private Task SendJsonMessageAsync(CancellationToken cancellationToken) {
            _jsonWriter.WriteEndObject();
            _jsonWriter.Flush();
            return _socket.SendAsync(
                new ArraySegment<byte>(_outputByteBuffer, 0, (int)_jsonOutputStream.Position),
                WebSocketMessageType.Text, true, cancellationToken
            );
        }

        public void Dispose() => _session.Dispose();

        JsonWriter ICommandResultSender.StartJsonMessage(string messageTypeName) => StartJsonMessage(messageTypeName);
        Task ICommandResultSender.SendJsonMessageAsync(CancellationToken cancellationToken) => SendJsonMessageAsync(cancellationToken);
    }
}
