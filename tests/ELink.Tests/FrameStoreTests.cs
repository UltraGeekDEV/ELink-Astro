using ELink.Contracts;
using ELink.Contracts.Composition;
using ELink.Core;
using Event.CoreFunctionality;
using Xunit;

namespace ELink.Tests;

/// <summary>Frames travel by reference inside a process: every subscriber of every hop gets the producer's array, and
/// EVent copies nothing of it (it used to cost twice the frame per subscriber).</summary>
public class FrameStoreTests
{
    [Fact]
    public async Task ASharedFrameReachesEverySubscriberAsTheSameArray()
    {
        using var node = ElinkNode.Create("FS-" + Guid.NewGuid().ToString("N")[..6], ELink.Testing.TestPorts.Next());
        var frame = new byte[8_000_000]; new Random(5).NextBytes(frame);
        var got = new List<byte[]>();
        for (int k = 0; k < 3; k++) await node.HookEventAsync("ELink.Test.Shot", (ShotEvent s) => { lock (got) got.Add(s.Data.Data); });
        var shot = new ShotEvent { Shooter = "cam", Format = ".fits", Data = RawBytes.Shared(frame) };
        await node.FireEventAsync("ELink.Test.Shot", shot);   // warm up
        got.Clear();
        GC.Collect(); GC.WaitForPendingFinalizers();
        long before = GC.GetTotalAllocatedBytes(true);
        await node.FireEventAsync("ELink.Test.Shot", shot);
        long allocated = GC.GetTotalAllocatedBytes(true) - before;
        Assert.Equal(3, got.Count);
        Assert.All(got, a => Assert.Same(frame, a));
        Assert.True(allocated < frame.Length / 4, $"firing an {frame.Length / 1e6:0} MB frame to 3 subscribers allocated {allocated / 1e6:0.0} MB");
    }

    private static RawBytes RoundTrip(RawBytes r)
    {
        Span<byte> wire = r.ToBytes();
        var back = new RawBytes();
        Assert.True(back.FromBytes(ref wire));
        return back;
    }

    [Fact]
    public async Task AReferenceIsResolvedHereOrFetchedFromTheNodeThatHasIt()
    {
        using var node = ElinkNode.Create("FS-" + Guid.NewGuid().ToString("N")[..6], ELink.Testing.TestPorts.Next());
        var frame = new byte[] { 1, 2, 3, 4, 5 };
        var shared = RawBytes.Shared(frame);
        // what a receiver in another process sees: the key, no bytes
        var far = RoundTrip(shared);
        Assert.Same(frame, far.Data);                                           // here it is the same array
        var remote = new RawBytes { Ref = shared.Ref };                          // there it would be empty
        Assert.True(remote.Remote);
        Assert.Equal(frame, await Frames.BytesAsync(node, remote));
        // asked over the mesh, the node answers with the bytes (as it would for another process)
        var answers = await node.CallFunctionAsync<EVent.Connections.Models.BaseBinaryConvertibles.BinaryConvertibleString, RawBytes>(Frames.GetId, (EVent.Connections.Models.BaseBinaryConvertibles.BinaryConvertibleString)shared.Ref!);
        Assert.Equal(frame, answers!.Single().Data);
        // a plain blob still travels as bytes
        var back = RoundTrip(new RawBytes(frame));
        Assert.Equal(frame, back.Data); Assert.Null(back.Ref);
    }
}
