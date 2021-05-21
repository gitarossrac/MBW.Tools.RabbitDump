using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks.Dataflow;
using MBW.Tools.RabbitDump.Movers;
using MBW.Tools.RabbitDump.Options;
using MBW.Tools.RabbitDump.Utilities;
using Microsoft.Extensions.Logging;

namespace MBW.Tools.RabbitDump.Tool
{
    class Dumper
    {
        private readonly ISource _source;
        private readonly IDestination _destination;
        private readonly ConsoleLifetime _hostLifetime;
        private readonly ILogger<Dumper> _logger;
        private readonly ArgumentsModel _model;

        public Dumper(ISource source, IDestination destination, ConsoleLifetime hostLifetime, ILogger<Dumper> logger, ArgumentsModel model)
        {
            _source = source;
            _destination = destination;
            _hostLifetime = hostLifetime;
            _logger = logger;
            _model = model;
        }

        public DumperExitCode Run()
        {
            if (_model.WithDebugger)
            {
                Debugger.Launch();
            }

            int count = 0;

            _logger.LogDebug("Begin moving data, with {Source} => {Destination}", _source, _destination);
            try
            {
                // Source => Buffer
                // Buffer => (black box target + acknowledger)

                BufferBlock<MessageItem> buffer = new BufferBlock<MessageItem>(new DataflowBlockOptions
                {
                    BoundedCapacity = 1000
                });

                (ITargetBlock<MessageItem> writer, IDataflowBlock finalBlock) targetWriter = _destination.GetWriter(_source);

                TransformBlock<MessageItem, MessageItem> countingBlock = new TransformBlock<MessageItem, MessageItem>(item =>
                {
                    count++;
                    if (count % 1000 == 0)
                        _logger.LogDebug("Sent {Count} messages to destination", count);

                    if (_model.Base64DecodeRebusHeaders)
                    {
                        Base64DecodeRebusHeaders(ref item);
                    }

                    return item;
                });

                countingBlock.LinkTo(targetWriter.writer, new DataflowLinkOptions
                {
                    PropagateCompletion = true
                });
                buffer.LinkTo(countingBlock, new DataflowLinkOptions
                {
                    PropagateCompletion = true
                });

                // Perform data feed
                _source.SendData(buffer, _hostLifetime.CancellationToken);
                buffer.Complete();

                // Wait for completion of writer
                TimeSpan waitTime = TimeSpan.FromSeconds(5);
                while (true)
                {
                    bool wasDone = targetWriter.finalBlock.Completion.Wait(waitTime);
                    if (wasDone)
                        break;

                    _logger.LogDebug("Waiting for destination to complete");
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "There was a generic error while copying data");
                return DumperExitCode.GenericError;
            }
            finally
            {
                (_source as IDisposable).TryDispose();
                (_destination as IDisposable).TryDispose();
            }

            _logger.LogInformation("Copied {Count} messages from source to destination", count);

            return DumperExitCode.Ok;
        }

        private void Base64DecodeRebusHeaders(ref MessageItem item)
        {
            foreach (var p in item.Properties)
            {
                if (!p.Key.StartsWith("rbs2")) continue;

                if (_model.InputType == InputType.Amqp && p.Value is byte[] bytes)
                {
                    item.Properties[p.Key] = Encoding.UTF8.GetString(bytes);
                }
                else
                {
                    var value = p.Value.ToString();
                    if (value != null)
                    {
                        item.Properties[p.Key] = Encoding.UTF8.GetString(Convert.FromBase64String(value));
                    }
                }
            }
        }
    }
}