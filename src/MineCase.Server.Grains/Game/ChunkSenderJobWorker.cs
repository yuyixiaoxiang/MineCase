﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MineCase.Protocol;
using MineCase.Server.Network;
using MineCase.Server.Network.Play;
using MineCase.Server.User;
using MineCase.Server.World;
using Orleans;
using Orleans.Concurrency;
using Orleans.Streams;

namespace MineCase.Server.Game
{
    public sealed class SendChunkJob
    {
        public IWorld World { get; set; }

        public int ChunkX { get; set; }

        public int ChunkZ { get; set; }

        public IReadOnlyCollection<IClientboundPacketSink> Clients { get; set; }

        public IReadOnlyCollection<IUser> Users { get; set; }
    }

    internal interface IChunkSenderJobWorker : IGrainWithGuidKey
    {
    }

    [ImplicitStreamSubscription(StreamProviders.Namespaces.ChunkSender)]
    internal class ChunkSenderJobWorker : Grain, IChunkSenderJobWorker
    {
        private readonly IPacketPackager _packetPackager;

        public ChunkSenderJobWorker(IPacketPackager packetPackager)
        {
            _packetPackager = packetPackager;
        }

        public override async Task OnActivateAsync()
        {
            var stream = GetStreamProvider(StreamProviders.JobsProvider).GetStream<SendChunkJob>(this.GetPrimaryKey(), StreamProviders.Namespaces.ChunkSender);
            await stream.SubscribeAsync(OnNextAsync);
        }

        private async Task OnNextAsync(SendChunkJob job, StreamSequenceToken token)
        {
            var chunkColumn = GrainFactory.GetGrain<IChunkColumn>(job.World.MakeChunkColumnKey(job.ChunkX, job.ChunkZ));

            var generator = new ClientPlayPacketGenerator(new BroadcastPacketSink(job.Clients, _packetPackager));
            await generator.ChunkData(Dimension.Overworld, job.ChunkX, job.ChunkZ, await chunkColumn.GetState());
            foreach (var user in job.Users)
                user.OnChunkSent(job.ChunkX, job.ChunkZ).Ignore();
        }

        private class BroadcastPacketSink : IPacketSink
        {
            private IReadOnlyCollection<IPacketSink> _sinks;
            private readonly IPacketPackager _packetPackager;

            public BroadcastPacketSink(IReadOnlyCollection<IPacketSink> sinks, IPacketPackager packetPackager)
            {
                _sinks = sinks ?? Array.Empty<IPacketSink>();
                _packetPackager = packetPackager;
            }

            public async Task SendPacket(ISerializablePacket packet)
            {
                if (_sinks.Any())
                {
                    var preparedPacket = await _packetPackager.PreparePacket(packet);
                    await SendPacket(preparedPacket.packetId, preparedPacket.data.AsImmutable());
                }
            }

            public Task SendPacket(uint packetId, Immutable<byte[]> data)
            {
                return Task.WhenAll(from sink in _sinks
                                    select sink.SendPacket(packetId, data));
            }
        }
    }
}
