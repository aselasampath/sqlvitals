using SqlVitals.Engine.Repositories;

namespace SqlVitals.Engine.Tests.Repositories;

public class SpTraceXmlParserTests
{
    private const string RpcCompletedXml = """
        <RingBufferTarget truncated="0" processingTime="0" totalEventsProcessed="42" eventCount="1" droppedCount="7" memoryUsed="4096">
          <event name="rpc_completed" package="sqlserver" timestamp="2026-09-18T10:22:31.412Z">
            <data name="duration"><value>842000</value></data>
            <data name="cpu_time"><value>120000</value></data>
            <data name="physical_reads"><value>3</value></data>
            <data name="logical_reads"><value>44102</value></data>
            <data name="writes"><value>11</value></data>
            <data name="row_count"><value>15</value></data>
            <data name="object_name"><value>usp_GetOrders</value></data>
            <data name="statement"><value>exec dbo.usp_GetOrders @CustId=8812</value></data>
            <action name="session_id" package="sqlserver"><value>71</value></action>
            <action name="database_name" package="sqlserver"><value>SalesDb</value></action>
            <action name="username" package="sqlserver"><value>svc_orders</value></action>
            <action name="client_app_name" package="sqlserver"><value>OrderSvc</value></action>
            <action name="client_hostname" package="sqlserver"><value>WEB01</value></action>
            <action name="attach_activity_id" package="package0"><value>A1B2C3D4-0001</value></action>
          </event>
        </RingBufferTarget>
        """;

    [Fact]
    public void Parse_ReadsAllEventFieldsAndActions()
    {
        var snapshot = SpTraceXmlParser.Parse(RpcCompletedXml);

        var ev = Assert.Single(snapshot.Events);
        Assert.Equal("rpc_completed", ev.EventName);
        Assert.Equal("usp_GetOrders", ev.ObjectName);
        Assert.Equal("SalesDb",       ev.DatabaseName);
        Assert.Equal(44102,           ev.LogicalReads);
        Assert.Equal(3,               ev.PhysicalReads);
        Assert.Equal(11,              ev.Writes);
        Assert.Equal(15,              ev.RowsReturned);
        Assert.Equal(71,              ev.SessionId);
        Assert.Equal("svc_orders",    ev.LoginName);
        Assert.Equal("OrderSvc",      ev.ClientAppName);
        Assert.Equal("WEB01",         ev.ClientHostName);
        Assert.Equal("A1B2C3D4-0001", ev.ActivityId);
    }

    [Fact]
    public void Parse_ConvertsMicrosecondDurationsToMilliseconds()
    {
        var ev = Assert.Single(SpTraceXmlParser.Parse(RpcCompletedXml).Events);

        Assert.Equal(842, ev.DurationMs);
        Assert.Equal(120, ev.CpuTimeMs);
    }

    [Fact]
    public void Parse_ReadsTargetHealthCounters()
    {
        var snapshot = SpTraceXmlParser.Parse(RpcCompletedXml);

        Assert.Equal(42, snapshot.TotalEventsProcessed);
        Assert.Equal(7,  snapshot.DroppedCount);
        Assert.False(snapshot.Truncated);
    }

    [Fact]
    public void Parse_FlagsTruncatedTarget()
    {
        var xml = RpcCompletedXml.Replace("truncated=\"0\"", "truncated=\"1\"");

        Assert.True(SpTraceXmlParser.Parse(xml).Truncated);
    }

    [Fact]
    public void Parse_TreatsMissingNumericFieldsAsZero()
    {
        // module_end does not expose logical_reads, physical_reads or writes at all.
        const string xml = """
            <RingBufferTarget truncated="0" droppedCount="0">
              <event name="module_end" package="sqlserver" timestamp="2026-09-18T10:22:31.412Z">
                <data name="duration"><value>5000</value></data>
                <data name="object_name"><value>usp_Recalc</value></data>
              </event>
            </RingBufferTarget>
            """;

        var ev = Assert.Single(SpTraceXmlParser.Parse(xml).Events);

        Assert.Equal(5, ev.DurationMs);
        Assert.Equal(0, ev.LogicalReads);
        Assert.Equal(0, ev.Writes);
        Assert.Equal(0, ev.CpuTimeMs);
        Assert.Null(ev.ClientAppName);
    }

    [Fact]
    public void Parse_RecoversEventsFromTruncatedXml()
    {
        // sys.dm_xe_session_targets can cut target_data mid-element. Complete events before
        // the cut must still be returned rather than the whole poll being lost.
        var cut = RpcCompletedXml.Replace("</RingBufferTarget>", "  <event name=\"rpc_comp");

        var snapshot = SpTraceXmlParser.Parse(cut);

        Assert.Single(snapshot.Events);
        Assert.Equal("usp_GetOrders", snapshot.Events[0].ObjectName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not xml at all")]
    [InlineData("<RingBufferTarget><event name=")]
    public void Parse_ReturnsEmptyForUnusableInput(string? xml)
    {
        var snapshot = SpTraceXmlParser.Parse(xml);

        Assert.Empty(snapshot.Events);
        Assert.Equal(0, snapshot.DroppedCount);
    }

    [Theory]
    [InlineData("exec dbo.usp_GetOrders @CustId=8812", "dbo.usp_GetOrders")]
    [InlineData("EXECUTE [dbo].[usp_Get Orders]",      "dbo.usp_Get Orders")]
    [InlineData("exec SalesDb.dbo.usp_GetOrders",      "dbo.usp_GetOrders")]
    [InlineData("exec usp_NoSchema",                   "usp_NoSchema")]
    [InlineData("SELECT 1",                            null)]
    [InlineData("",                                    null)]
    [InlineData(null,                                  null)]
    public void ExtractProcedureName_ReadsNameFromStatementText(string? statement, string? expected)
    {
        Assert.Equal(expected, SpTraceXmlParser.ExtractProcedureName(statement));
    }

    [Fact]
    public void ExtractProcedureName_IsUsedWhenObjectNameIsAbsent()
    {
        const string xml = """
            <RingBufferTarget truncated="0" droppedCount="0">
              <event name="rpc_completed" package="sqlserver" timestamp="2026-09-18T10:22:31.412Z">
                <data name="statement"><value>exec dbo.usp_Fallback @x=1</value></data>
              </event>
            </RingBufferTarget>
            """;

        var ev = Assert.Single(SpTraceXmlParser.Parse(xml).Events);

        Assert.Equal("dbo.usp_Fallback", ev.ObjectName);
    }

    [Fact]
    public void Parse_PrefersTheFriendlyTextFormForObjectType()
    {
        // object_type is a map: <value> is the numeric key, <text> the readable name.
        const string xml = """
            <RingBufferTarget truncated="0" droppedCount="0">
              <event name="module_end" package="sqlserver" timestamp="2026-09-18T10:22:31.412Z">
                <data name="object_type"><value>8272</value><text>P</text></data>
                <data name="object_name"><value>usp_Recalc</value></data>
              </event>
            </RingBufferTarget>
            """;

        var ev = Assert.Single(SpTraceXmlParser.Parse(xml).Events);

        Assert.Equal("P", ev.ObjectType);
        Assert.True(ev.IsStoredProcedure);
    }

    [Theory]
    [InlineData("P",    true)]    // stored procedure
    [InlineData("PC",   true)]    // CLR stored procedure
    [InlineData("8272", true)]    // numeric form, when no <text> was present
    [InlineData("TR",   false)]   // trigger
    [InlineData("FN",   false)]   // scalar function
    [InlineData("TF",   false)]   // table-valued function
    public void IsStoredProcedure_ClassifiesModuleEndByObjectType(string objectType, bool expected)
    {
        var xml = $"""
            <RingBufferTarget truncated="0" droppedCount="0">
              <event name="module_end" package="sqlserver" timestamp="2026-09-18T10:22:31.412Z">
                <data name="object_type"><text>{objectType}</text></data>
              </event>
            </RingBufferTarget>
            """;

        var ev = Assert.Single(SpTraceXmlParser.Parse(xml).Events);

        Assert.Equal(expected, ev.IsStoredProcedure);
    }

    [Fact]
    public void IsStoredProcedure_KeepsRpcCompletedRegardlessOfObjectType()
    {
        // rpc_completed only ever fires for procedure calls and carries no object_type.
        var ev = Assert.Single(SpTraceXmlParser.Parse(RpcCompletedXml).Events);

        Assert.Null(ev.ObjectType);
        Assert.True(ev.IsStoredProcedure);
    }

    [Fact]
    public void IsStoredProcedure_KeepsModuleEndWhenObjectTypeWasNotCaptured()
    {
        // Better to show an extra row than to silently drop a real procedure call.
        const string xml = """
            <RingBufferTarget truncated="0" droppedCount="0">
              <event name="module_end" package="sqlserver" timestamp="2026-09-18T10:22:31.412Z">
                <data name="object_name"><value>usp_Unknown</value></data>
              </event>
            </RingBufferTarget>
            """;

        var ev = Assert.Single(SpTraceXmlParser.Parse(xml).Events);

        Assert.Null(ev.ObjectType);
        Assert.True(ev.IsStoredProcedure);
    }

    [Fact]
    public void DedupKey_UsesActivityIdWhenCausalityTrackingIsOn()
    {
        var ev = Assert.Single(SpTraceXmlParser.Parse(RpcCompletedXml).Events);

        Assert.Equal("rpc_completed|A1B2C3D4-0001", ev.DedupKey);
    }

    [Fact]
    public void DedupKey_SeparatesDistinctCallsWhenActivityIdIsAbsent()
    {
        const string xml = """
            <RingBufferTarget truncated="0" droppedCount="0">
              <event name="rpc_completed" package="sqlserver" timestamp="2026-09-18T10:22:31.412Z">
                <data name="duration"><value>1000</value></data>
                <action name="session_id" package="sqlserver"><value>51</value></action>
              </event>
              <event name="rpc_completed" package="sqlserver" timestamp="2026-09-18T10:22:31.412Z">
                <data name="duration"><value>9000</value></data>
                <action name="session_id" package="sqlserver"><value>52</value></action>
              </event>
            </RingBufferTarget>
            """;

        var events = SpTraceXmlParser.Parse(xml).Events;

        Assert.Equal(2, events.Count);
        Assert.NotEqual(events[0].DedupKey, events[1].DedupKey);
    }
}
