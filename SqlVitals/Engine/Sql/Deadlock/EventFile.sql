SELECT event_data AS EventData
FROM sys.fn_xe_file_target_read_file(@pattern, NULL, NULL, NULL)
WHERE object_name = N'xml_deadlock_report'
