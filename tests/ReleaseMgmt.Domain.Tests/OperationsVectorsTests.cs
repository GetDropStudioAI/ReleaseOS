using ReleaseMgmt.Domain.Services;

namespace ReleaseMgmt.Domain.Tests;

/// <summary>
/// Fixed vectors for the two formats other software has to reproduce: the audit chain's canonical form and hash (REOS-114; tools/verify_audit_chain.py does) and the control webhook signature
/// (REOS-112; a receiver does). The expected values were computed independently in Python, so a change to either format fails here before it breaks a sealed row or a receiver.
/// </summary>
public class OperationsVectorsTests
{
    [Fact]
    public void The_canonical_form_of_an_audit_row_is_a_version_line_then_each_column_with_its_utf8_byte_length_or_a_dash_for_null()
    {
        var canonical = AuditChain.Canonical(7, "2026-10-20T14:00:00Z", "u1", "t1", "GateWaiver", "w1", "Decline", null, "{\"note\":\"Zürich ✓\"}", "default");
        Assert.Equal("ReleaseOS.Audit.v1\n1:7\n20:2026-10-20T14:00:00Z\n2:u1\n2:t1\n10:GateWaiver\n2:w1\n7:Decline\n-\n22:{\"note\":\"Zürich ✓\"}\n7:default", canonical);   // 22 bytes, not 18 characters
    }

    [Fact]
    public void The_chain_hashes_the_previous_hash_and_the_canonical_form_starting_from_64_zeros()
    {
        Assert.Equal(new string('0', 64), AuditChain.Genesis);
        var first = AuditChain.Hash(AuditChain.Genesis, AuditChain.Canonical(7, "2026-10-20T14:00:00Z", "u1", "t1", "GateWaiver", "w1", "Decline", null, "{\"note\":\"Zürich ✓\"}", "default"));
        Assert.Equal("87432d4727c5200c65fb223020bce7eb363fdafc8be91ba9adc9159b47edeac5", first);
        var second = AuditChain.Hash(first, AuditChain.Canonical(8, "2026-10-20T14:00:01Z", null, null, "Train", "t1", "Create", null, null, null));
        Assert.Equal("7860e1e6bcecaab8509abdd0a2cdd345669d3b21cd15e06eaef85fe750018947", second);
        Assert.Equal("*", AuditChain.ChainOf(null));
        Assert.Equal("w1", AuditChain.ChainOf("w1"));
    }

    [Fact]
    public void A_control_call_is_signed_over_the_timestamp_the_delivery_id_and_the_exact_body()
    {
        const string body = "{\"id\":\"a1\",\"action\":\"pause\"}";
        Assert.Equal("v1=3acbc3cd88207e00a25d13895fb3e986c686332fe23417adf32ee34dee7ff25e", ControlSignature.Sign("whs_secret", 1792504800, "a1", body));
        Assert.NotEqual(ControlSignature.Sign("whs_secret", 1792504800, "a1", body), ControlSignature.Sign("whs_secret", 1792504801, "a1", body));
        Assert.NotEqual(ControlSignature.Sign("whs_secret", 1792504800, "a1", body), ControlSignature.Sign("whs_secret", 1792504800, "a2", body));
        Assert.NotEqual(ControlSignature.Sign("whs_secret", 1792504800, "a1", body), ControlSignature.Sign("whs_secret", 1792504800, "a1", body + " "));
        Assert.Equal(("X-ReleaseOS-Delivery", "X-ReleaseOS-Timestamp", "X-ReleaseOS-Signature", 300), (ControlSignature.DeliveryHeader, ControlSignature.TimestampHeader, ControlSignature.SignatureHeader, ControlSignature.ToleranceSeconds));
    }

    [Fact]
    public async Task The_seal_signal_never_blocks_and_one_pending_signal_absorbs_the_rest()
    {
        for (var i = 0; i < 100; i++) AuditSealSignal.Notify();   // a burst of commits
        Assert.True(await AuditSealSignal.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.False(await AuditSealSignal.WaitAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None));   // one pass takes them all
    }
}
