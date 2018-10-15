namespace ReactiveDomain.Transport.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.NetworkInformation;
    using System.Threading;
    using Newtonsoft.Json;
    using ReactiveDomain.Messaging;
    using ReactiveDomain.Messaging.Bus;
    using Xunit;

    public class TcpBigMessageTest
    {
        readonly IPAddress _hostAddress;
        readonly IDispatcher _commandBus;
        readonly MockTcpConnection _clientTcpConnection;
        readonly int _commandPort = 10660;

        public TcpBigMessageTest()
        {
            _commandBus = new Dispatcher("TestBus");
            _hostAddress = IPAddress.Loopback;
            _commandPort = GetAvailablePort(1000);
        }

        class TcpServer : TcpBusServerSide, IDisposable
        {
            readonly TcpInboundMessageHandler _tcpInboundMessageHandler;
            readonly TcpOutboundMessageHandler _tcpOutboundMessageHandler;
            readonly List<Type> _inboundSpamMessageTypes;
            readonly QueuedHandlerDiscarding _inboundSpamMessageQueuedHandler;
            readonly QueuedHandler _inboundMessageQueuedHandler;
            readonly QueuedHandler _outboundMessageQueuedHandler;

            public TcpServer(IPAddress iP, int port, IDispatcher bus)
                :base(iP, port, bus)
            { 
                _outboundMessageQueuedHandler = new QueuedHandler(
                    this,
                    "OutboundMessageQueuedHandler",
                    true,
                    TimeSpan.FromMilliseconds(1000)
                );

                _tcpOutboundMessageHandler = new TcpOutboundMessageHandler(
                    bus,
                    _outboundMessageQueuedHandler
                );

                _tcpInboundMessageHandler = new TcpInboundMessageHandler(
                    bus,
                    _tcpOutboundMessageHandler
                );

                _inboundSpamMessageQueuedHandler = new QueuedHandlerDiscarding(
                    _tcpInboundMessageHandler,
                    "InboundSpamMessageQueuedHandler",
                    true,
                    TimeSpan.FromMilliseconds(1000)
                );

                _inboundMessageQueuedHandler = new QueuedHandler(
                    _tcpInboundMessageHandler,
                    "InboundMessageQueuedHandler",
                    true,
                    TimeSpan.FromMilliseconds(1000)
                );

                // The TcpBusSide classes all support "inbound spam messages".  We have some of those that
                // go from server to client, but none that go from client to server.  Hence, this list is empty.
                _inboundSpamMessageTypes = new List<Type>();

                // Because of the way that Commands generate CommandResponses, and the CommandResponses
                // are transmitted in the opposite direction of the Command, we end up with circular
                // references.  These 3 statements make the circularity.
                this.InboundSpamMessageQueuedHandler = _inboundSpamMessageQueuedHandler;
                this.InboundSpamMessageTypes = _inboundSpamMessageTypes;
                this.InboundMessageQueuedHandler = _inboundMessageQueuedHandler;
            }

            public void Start()
            {
                _inboundSpamMessageQueuedHandler.Start();
                _inboundMessageQueuedHandler.Start();
                _outboundMessageQueuedHandler.Start();
            }

            public void Dispose()
            {
                _inboundSpamMessageQueuedHandler.Stop();
                _inboundMessageQueuedHandler.Stop();
                _outboundMessageQueuedHandler.Stop();
            }
        }

        class TcpClient : TcpBusClientSide, IDisposable
        {
            readonly TcpInboundMessageHandler _tcpInboundMessageHandler;
            readonly TcpOutboundMessageHandler _tcpOutboundMessageHandler;
            readonly List<Type> _inboundSpamMessageTypes;
            readonly QueuedHandlerDiscarding _inboundSpamMessageQueuedHandler;
            readonly QueuedHandler _inboundMessageQueuedHandler;
            readonly QueuedHandler _outboundMessageQueuedHandler;

            public TcpClient(IDispatcher messageBus, IPAddress hostIP, int commandPort, ITcpConnection tcpConnection = null) 
                : base(messageBus, hostIP, commandPort, tcpConnection)
            { 
                _outboundMessageQueuedHandler = new QueuedHandler(
                    this,
                    "OutboundMessageQueuedHandler",
                    true,
                    TimeSpan.FromMilliseconds(1000)
                );

                _tcpOutboundMessageHandler = new TcpOutboundMessageHandler(
                    messageBus,
                    _outboundMessageQueuedHandler);

                _tcpInboundMessageHandler = new TcpInboundMessageHandler(
                    messageBus,
                    _tcpOutboundMessageHandler);
                _inboundSpamMessageQueuedHandler = new QueuedHandlerDiscarding(
                    _tcpInboundMessageHandler,
                    "InboundSpamMessageQueuedHandler",
                    true,
                    TimeSpan.FromMilliseconds(1000));
                _inboundMessageQueuedHandler = new QueuedHandler(
                    _tcpInboundMessageHandler,
                    "InboundMessageQueuedHandler",
                    true,
                    TimeSpan.FromMilliseconds(1000));

                // When these messages back up in the discarding queue, just throw them away.  Out here
                // on the client, we don't care if we miss some of these (so long as we always get the
                // LAST one).
                _inboundSpamMessageTypes = new List<Type>();

                // Because of the way that Commands generate CommandResponses, and the CommandResponses
                // are transmitted in the opposite direction of the Command, we end up with circular
                // references.  These 4 statements make the circularity.
                this.InboundSpamMessageQueuedHandler = _inboundSpamMessageQueuedHandler;
                this.InboundSpamMessageTypes = _inboundSpamMessageTypes;
                this.InboundMessageQueuedHandler = _inboundMessageQueuedHandler;

                _inboundSpamMessageQueuedHandler.Start();
                _inboundMessageQueuedHandler.Start();
                _outboundMessageQueuedHandler.Start();
            }

            public void Dispose()
            {
                _inboundSpamMessageQueuedHandler.Stop();
                _inboundMessageQueuedHandler.Stop();
                _outboundMessageQueuedHandler.Stop();
            }
        }

        class TestCommand : Command
        {
            public TestCommand()
            : this(NewRoot())
            { }

            public TestCommand(CorrelationId correlationId, SourceId sourceId)
                : base(correlationId, sourceId)
            { }

            public TestCommand(CorrelatedMessage source)
                : base(source)
            { }

            public Guid Id { get; set; }

            public Dictionary<int, string> Fields { get; set; }
        }

        class TestHandler : IHandle<TestMessage>
        {
            private volatile int _received;

            public void Handle(TestMessage msg)
            {
                Interlocked.Increment(ref _received);
            }

            public int Received => _received;
        }

        static int GetAvailablePort(int startingPort)
        {
            var portArray = new List<int>();

            var properties = IPGlobalProperties.GetIPGlobalProperties();

            //getting active connections
            var connections = properties.GetActiveTcpConnections();
            portArray.AddRange(
                connections.Where(n => n.LocalEndPoint.Port >= startingPort).Select(n => n.LocalEndPoint.Port));

            //getting active tcp listners - WCF service listening in tcp
            var endPoints = properties.GetActiveTcpListeners();
            portArray.AddRange(endPoints.Where(n => n.Port >= startingPort).Select(n => n.Port));

            //getting active udp listeners
            endPoints = properties.GetActiveUdpListeners();
            portArray.AddRange(endPoints.Where(n => n.Port >= startingPort).Select(n => n.Port));

            portArray.Sort();

            for (var i = startingPort; i < ushort.MaxValue; i++)
                if (!portArray.Contains(i))
                    return i;

            return 0;
        }

        public sealed class TestMessage : Message
        {
            [JsonConstructor]
            public TestMessage(byte[] data)
            {
                Data = data;
            }

            public TestMessage(int size)
            {
                Data = new byte[size];

                for (var i = 0; i < size; ++i)
                    Data[i] = (byte) (i % 256);
            }

            public byte[] Data { get; }
        }

        void TestSend(int msgSize, int msgCount, TimeSpan timeout)
        {
            var handler = new TestHandler();
            _commandBus.Subscribe<TestMessage>(handler);

            using (var tcpBusServerSide = new TcpServer(_hostAddress, _commandPort, _commandBus))
            {
                tcpBusServerSide.Start();

                var clientBus = new Dispatcher("Client Bus", slowMsgThreshold: TimeSpan.FromMinutes(60), slowCmdThreshold: TimeSpan.FromMinutes(60));

                using (new TcpClient(clientBus, _hostAddress, _commandPort))
                {
                    for (var i = 0; i < msgCount; ++i)
                    {
                        var msg = new TestMessage(msgSize);

                        var ex = Record.Exception(() => clientBus.Publish(msg));
                        Assert.Null(ex);
                    }

                    var start = DateTime.Now;
                    while (DateTime.Now - start < timeout)
                    {
                        if (msgCount == handler.Received)
                            break;
                        Thread.Sleep(100);
                    }

                    Assert.Equal(msgCount, handler.Received);
                }
            }
        }

        [Fact]
        public void can_send_few_small_messages()
        {
            TestSend(10, 128, TimeSpan.FromSeconds(1));
        }

        [Fact]
        public void can_send_many_small_messages()
        {
            TestSend(2000, 128, TimeSpan.FromSeconds(5));
        }

        [Fact]
        public void can_send_few_large_messages()
        {
            TestSend(10, 12 * 1024, TimeSpan.FromSeconds(10));
        }

        [Fact]
        public void can_send_many_large_messages()
        {
            TestSend(2000, 12 * 1024, TimeSpan.FromSeconds(10));
        }
    }
}
