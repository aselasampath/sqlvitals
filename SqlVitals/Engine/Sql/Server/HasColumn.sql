SELECT CASE WHEN EXISTS (SELECT 1 FROM sys.all_columns
                         WHERE object_id = OBJECT_ID(N'/*VIEW*/') AND name = N'/*COLUMN*/')
            THEN 1 ELSE 0 END AS HasColumn
