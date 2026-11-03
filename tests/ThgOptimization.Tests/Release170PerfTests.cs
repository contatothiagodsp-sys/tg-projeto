using System.Diagnostics;
using ThgOptimization.Core.Oc;
using ThgOptimization.Infrastructure;

namespace ThgOptimization.Tests;

#pragma warning disable THGOC001 // só o formato do pedido, sem driver
public class PawnIoRequestTests
{
    [Fact]
    public void Request_has_the_name_padded_to_32_bytes_and_then_the_values()
    {
        Span<byte> request = stackalloc byte[PawnIoTransport.RequestSize(2)];
        PawnIoTransport.BuildRequest("ioctl_read", stackalloc ulong[] { 0x1122334455667788, 2 }, request);
        Assert.Equal(48, request.Length);
        Assert.Equal((byte)'i', request[0]);
        Assert.Equal(0, request[10]);
        Assert.Equal(0, request[31]);
        Assert.Equal(0x88, request[32]);
        Assert.Equal(0x11, request[39]);
        Assert.Equal(2, request[40]);
    }

    [Fact]
    public void Building_the_request_does_not_allocate()
    {
        PawnIoTransport.BuildRequest("ioctl_read_smu_register", stackalloc ulong[] { 1 }, stackalloc byte[PawnIoTransport.RequestSize(1)]);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
            PawnIoTransport.BuildRequest("ioctl_read_smu_register", stackalloc ulong[] { 1 }, stackalloc byte[PawnIoTransport.RequestSize(1)]);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
#pragma warning restore THGOC001
