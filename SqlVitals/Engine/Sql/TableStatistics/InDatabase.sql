DECLARE @proc nvarchar(300) = QUOTENAME(@Database) + N'.sys.sp_executesql';
EXEC @proc @Sql, N'@ObjectId int', @ObjectId = @ObjectId
