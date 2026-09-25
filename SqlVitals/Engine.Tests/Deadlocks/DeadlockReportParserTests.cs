using System.Xml.Linq;
using SqlVitals.Engine.Deadlocks;

namespace SqlVitals.Engine.Tests.Deadlocks;

public class DeadlockReportParserTests
{
    // Two sessions updating Orders and OrderLines in opposite order: the usual deadlock, in the
    // form system_health writes it on SQL Server 2012 and later.
    internal const string KeyLockEvent = """
        <event name="xml_deadlock_report" package="sqlserver" timestamp="2026-09-20T08:15:30.123Z">
          <data name="xml_report">
            <type name="xml" package="package0" />
            <value>
              <deadlock>
                <victim-list>
                  <victimProcess id="process1a" />
                </victim-list>
                <process-list>
                  <process id="process1a" taskpriority="0" logused="264" waitresource="KEY: 5:72057594043236352 (8194443284a0)"
                           waittime="3120" ownerId="12345" transactionname="user_transaction" lockMode="U" status="suspended"
                           spid="55" trancount="2" lastbatchstarted="2026-09-20T10:15:25.100" clientapp="Billing Service"
                           hostname="APP01" loginname="billing_app" isolationlevel="read committed (2)" currentdb="5"
                           currentdbname="Sales">
                    <executionStack>
                      <frame procname="Sales.dbo.usp_ShipOrder" line="12" stmtstart="480" stmtend="700" sqlhandle="0x03000500">
        UPDATE dbo.Orders SET Status = 'Shipped' WHERE OrderId = @OrderId
                      </frame>
                      <frame procname="adhoc" line="1" sqlhandle="0x01000500">
        unknown
                      </frame>
                    </executionStack>
                    <inputbuf>
        EXEC dbo.usp_ShipOrder @OrderId = 42
                    </inputbuf>
                  </process>
                  <process id="process2b" taskpriority="0" logused="512" waitresource="KEY: 5:72057594043301888 (a0c2f7ad5b1e)"
                           waittime="3050" transactionname="user_transaction" lockMode="U" status="suspended" spid="61"
                           trancount="2" clientapp="Warehouse UI" hostname="WH02" loginname="warehouse"
                           isolationlevel="read committed (2)" currentdb="5" currentdbname="Sales">
                    <executionStack>
                      <frame procname="Sales.dbo.usp_PickLines" line="8" sqlhandle="0x03000600">
        UPDATE dbo.OrderLines SET Picked = 1 WHERE OrderId = @OrderId
                      </frame>
                    </executionStack>
                    <inputbuf>
        Proc [Database Id = 5 Object Id = 1237579447]
                    </inputbuf>
                  </process>
                </process-list>
                <resource-list>
                  <keylock hobtid="72057594043301888" dbid="5" objectname="Sales.dbo.OrderLines" indexname="PK_OrderLines"
                           id="lock1" mode="X" associatedObjectId="72057594043301888">
                    <owner-list>
                      <owner id="process1a" mode="X" />
                    </owner-list>
                    <waiter-list>
                      <waiter id="process2b" mode="U" requestType="wait" />
                    </waiter-list>
                  </keylock>
                  <keylock hobtid="72057594043236352" dbid="5" objectname="Sales.dbo.Orders" indexname="PK_Orders"
                           id="lock2" mode="X" associatedObjectId="72057594043236352">
                    <owner-list>
                      <owner id="process2b" mode="X" />
                    </owner-list>
                    <waiter-list>
                      <waiter id="process1a" mode="U" requestType="wait" />
                    </waiter-list>
                  </keylock>
                </resource-list>
              </deadlock>
            </value>
          </data>
        </event>
        """;

    [Fact]
    public void Reads_the_victim_the_other_participant_and_what_they_ran()
    {
        var report = DeadlockReportParser.ParseEvent(KeyLockEvent)!;

        Assert.Equal(new DateTime(2026, 9, 20, 8, 15, 30, 123, DateTimeKind.Utc), report.TimestampUtc);
        Assert.Equal(DateTimeKind.Utc, report.TimestampUtc.Kind);

        var victim = Assert.Single(report.Victims);
        Assert.Equal(55, victim.SessionId);
        Assert.Equal("billing_app", victim.LoginName);
        Assert.Equal("APP01", victim.HostName);
        Assert.Equal("Billing Service", victim.ClientApp);
        Assert.Equal("Sales", victim.DatabaseName);
        Assert.Equal("read committed (2)", victim.IsolationLevel);
        Assert.Equal(3120, victim.WaitTimeMs);
        Assert.Equal(2, victim.TranCount);
        Assert.Equal("Sales.dbo.usp_ShipOrder", victim.ProcedureName);
        Assert.Equal("UPDATE dbo.Orders SET Status = 'Shipped' WHERE OrderId = @OrderId", victim.Statement);
        Assert.Equal("EXEC dbo.usp_ShipOrder @OrderId = 42", victim.InputBuffer);
        Assert.Equal(new DateTime(2026, 9, 20, 10, 15, 25, 100), victim.LastBatchStarted);

        var other = Assert.Single(report.Others);
        Assert.Equal(61, other.SessionId);
        Assert.Equal("Sales.dbo.usp_PickLines", other.ProcedureName);

        Assert.Equal("SPID 55 · billing_app · Sales.dbo.usp_ShipOrder", report.VictimText);
        Assert.Equal("SPID 61 · warehouse · Sales.dbo.usp_PickLines", report.OthersText);
        Assert.Equal("UPDATE dbo.Orders SET Status = 'Shipped' WHERE OrderId = @OrderId", report.VictimStatement);
        Assert.Equal("Sales", report.DatabasesText);
        Assert.Equal(2, report.SessionCount);
    }

    [Fact]
    public void Reads_the_objects_and_who_held_and_waited_for_each_lock()
    {
        var report = DeadlockReportParser.ParseEvent(KeyLockEvent)!;

        Assert.Equal("Sales.dbo.OrderLines (PK_OrderLines); Sales.dbo.Orders (PK_Orders)", report.ObjectsText);

        var lines = report.Resources[0];
        Assert.Equal("Key", lines.Kind);
        Assert.Equal("Sales.dbo.OrderLines", lines.ObjectName);
        Assert.Equal("PK_OrderLines", lines.IndexName);
        Assert.Equal("hobt 72057594043301888", lines.Detail);
        Assert.Equal("SPID 55 (X)", lines.OwnersText);
        Assert.Equal("SPID 61 (U)", lines.WaitersText);

        var victim = report.Victims.Single();
        Assert.Equal("U on Sales.dbo.Orders (PK_Orders)", victim.WaitingFor);
        Assert.Equal("X on Sales.dbo.OrderLines (PK_OrderLines)", victim.Holding);
    }

    [Fact]
    public void Keeps_the_deadlock_element_as_the_xdl_content()
    {
        var report = DeadlockReportParser.ParseEvent(KeyLockEvent)!;

        var xdl = XElement.Parse(report.Xml);
        Assert.Equal("deadlock", xdl.Name.LocalName);
        Assert.Equal(2, xdl.Element("process-list")!.Elements("process").Count());
        Assert.NotNull(xdl.Element("victim-list"));
        Assert.NotNull(xdl.Element("resource-list"));
    }

    [Fact]
    public void Falls_back_to_the_input_buffer_when_the_stack_has_no_text()
    {
        const string xml = """
            <event name="xml_deadlock_report" timestamp="2026-09-20T08:15:30Z">
              <data name="xml_report"><value>
                <deadlock>
                  <victim-list><victimProcess id="p1" /></victim-list>
                  <process-list>
                    <process id="p1" spid="70" loginname="etl">
                      <executionStack>
                        <frame procname="adhoc" line="1" sqlhandle="0x02" />
                        <frame procname="unknown" line="1" sqlhandle="0x03">unknown</frame>
                      </executionStack>
                      <inputbuf>DELETE FROM dbo.Staging WHERE BatchId = 7</inputbuf>
                    </process>
                  </process-list>
                  <resource-list />
                </deadlock>
              </value></data>
            </event>
            """;

        var victim = DeadlockReportParser.ParseEvent(xml)!.Processes.Single();

        Assert.Null(victim.ProcedureName);
        Assert.Null(victim.Statement);
        Assert.Equal("DELETE FROM dbo.Staging WHERE BatchId = 7", victim.CodeText);
        Assert.Equal("DELETE FROM dbo.Staging WHERE BatchId = 7",
                     DeadlockReportParser.ParseEvent(xml)!.VictimStatement);
    }

    [Fact]
    public void Reads_a_parallel_deadlock_with_several_victims_as_one_session()
    {
        // One query's parallel workers deadlocked on exchange ports: every process is SPID 80.
        const string xml = """
            <event name="xml_deadlock_report" timestamp="2026-09-21T01:00:00Z">
              <data name="xml_report"><value>
                <deadlock>
                  <victim-list>
                    <victimProcess id="p1" />
                    <victimProcess id="p2" />
                  </victim-list>
                  <process-list>
                    <process id="p1" spid="80" ecid="1" loginname="report" currentdbname="DW">
                      <executionStack><frame procname="adhoc" line="1">SELECT COUNT(*) FROM dbo.Fact</frame></executionStack>
                    </process>
                    <process id="p2" spid="80" ecid="2" loginname="report" currentdbname="DW">
                      <executionStack><frame procname="adhoc" line="1">SELECT COUNT(*) FROM dbo.Fact</frame></executionStack>
                    </process>
                    <process id="p3" spid="80" ecid="0" loginname="report" currentdbname="DW" />
                  </process-list>
                  <resource-list>
                    <exchangeEvent id="Pipe1" WaitType="e_waitPipeGetRow" nodeId="4">
                      <owner-list><owner id="p3" /></owner-list>
                      <waiter-list><waiter id="p1" /></waiter-list>
                    </exchangeEvent>
                  </resource-list>
                </deadlock>
              </value></data>
            </event>
            """;

        var report = DeadlockReportParser.ParseEvent(xml)!;

        Assert.Equal(2, report.Victims.Count());
        Assert.Equal(1, report.SessionCount);
        Assert.Equal("SPID 80 · report · SELECT COUNT(*) FROM dbo.Fact", report.VictimText);

        var exchange = Assert.Single(report.Resources);
        Assert.Equal("Parallel exchange", exchange.Kind);
        Assert.Equal("e_waitPipeGetRow node 4", exchange.Detail);
        Assert.Equal("Parallel exchange", report.ObjectsText);
        Assert.Equal("SPID 80", exchange.OwnersText);
    }

    [Fact]
    public void Reads_the_SQL_Server_2008_form_with_the_victim_on_the_deadlock_element()
    {
        const string xml = """
            <event name="xml_deadlock_report" timestamp="2012-01-05T10:00:00.000Z">
              <data name="xml_report"><value>
                <deadlock-list>
                  <deadlock victim="processA">
                    <process-list>
                      <process id="processA" spid="52" currentdb="7" />
                      <process id="processB" spid="53" currentdb="7" />
                    </process-list>
                    <resource-list>
                      <pagelock fileid="1" pageid="1880" dbid="7" objectname="Inventory.dbo.Stock" id="lockA" mode="IX">
                        <owner-list><owner id="processB" mode="IX" /></owner-list>
                        <waiter-list><waiter id="processA" mode="S" requestType="wait" /></waiter-list>
                      </pagelock>
                    </resource-list>
                  </deadlock>
                </deadlock-list>
              </value></data>
            </event>
            """;

        var report = DeadlockReportParser.ParseEvent(xml)!;

        Assert.Equal(52, report.Victims.Single().SessionId);
        Assert.Equal("database id 7", report.Victims.Single().DatabaseName);
        Assert.Equal("Page", report.Resources[0].Kind);
        Assert.Equal("page 1:1880", report.Resources[0].Detail);
        Assert.Equal("S on Inventory.dbo.Stock", report.Victims.Single().WaitingFor);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<event name=\"xml_deadlock_report\" timestamp=\"2026-09-20T08:15:30Z\"><data")]   // cut off
    [InlineData("<event name=\"lock_timeout\" timestamp=\"2026-09-20T08:15:30Z\" />")]            // another event
    [InlineData("<event name=\"xml_deadlock_report\"><data><value><deadlock><process-list><process id=\"p\" /></process-list></deadlock></value></data></event>")]  // no time
    [InlineData("<event name=\"xml_deadlock_report\" timestamp=\"2026-09-20T08:15:30Z\"><data><value><deadlock /></value></data></event>")]  // no processes
    public void Returns_null_for_anything_that_is_not_a_readable_report(string? xml)
    {
        Assert.Null(DeadlockReportParser.ParseEvent(xml));
    }

    [Fact]
    public void Reads_only_the_deadlocks_from_a_ring_buffer_even_when_it_was_cut_off()
    {
        var targetData = $"""
            <RingBufferTarget truncated="1" processingTime="0" totalEventsProcessed="4" eventCount="4" droppedCount="0" memoryUsed="1">
              <event name="sp_server_diagnostics_component_result" timestamp="2026-09-20T08:00:00Z"><data name="component"><text>SYSTEM</text></data></event>
              {KeyLockEvent}
              <event name="xml_deadlock_report" timestamp="2026-09-20T09:00:00Z"><data><value><deadlock /></value></data></event>
              {KeyLockEvent.Replace("2026-09-20T08:15:30.123Z", "2026-09-20T09:30:00Z")}
              <event name="xml_deadlock_report" timestamp="2026-09-20T10:00:00Z"><data name="xml_report"><value><deadl
            """;

        var reports = DeadlockReportParser.ParseRingBuffer(targetData, out var unreadable);

        Assert.Equal(2, reports.Count);
        Assert.Equal(1, unreadable);   // the one with no processes; the cut-off one is dropped with the tail
        Assert.Equal(new DateTime(2026, 9, 20, 9, 30, 0, DateTimeKind.Utc), reports[1].TimestampUtc);
    }

    [Fact]
    public void An_empty_ring_buffer_has_no_deadlocks()
    {
        Assert.Empty(DeadlockReportParser.ParseRingBuffer(null, out _));
        Assert.Empty(DeadlockReportParser.ParseRingBuffer("<RingBufferTarget />", out var unreadable));
        Assert.Equal(0, unreadable);
    }

    [Theory]
    [InlineData(@"C:\Program Files\Microsoft SQL Server\MSSQL16.MSSQLSERVER\MSSQL\Log\system_health_0_133712345678900000.xel",
                @"C:\Program Files\Microsoft SQL Server\MSSQL16.MSSQLSERVER\MSSQL\Log\system_health*.xel")]
    [InlineData("/var/opt/mssql/log/system_health_0_133712345678900000.xel", "/var/opt/mssql/log/system_health*.xel")]
    [InlineData(@"D:\Logs\system_health.xel", @"D:\Logs\system_health*.xel")]
    public void Reads_every_rollover_file_in_the_folder_system_health_writes_to(string current, string expected)
    {
        var targetData = $"""<EventFileTarget truncated="0"><Buffers logged="12" dropped="0" /><File name="{current}" /></EventFileTarget>""";

        Assert.Equal(expected, DeadlockReportParser.EventFilePattern(targetData));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<EventFileTarget truncated=\"0\" />")]
    [InlineData("not xml")]
    public void Falls_back_to_the_log_folder_when_the_file_name_is_unknown(string? targetData)
    {
        Assert.Equal("system_health*.xel", DeadlockReportParser.EventFilePattern(targetData));
    }
}
