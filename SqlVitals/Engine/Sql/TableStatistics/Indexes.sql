SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
SET LOCK_TIMEOUT 2000;
SELECT i.index_id AS IndexId, i.name AS IndexName, i.type_desc AS IndexType,
    i.is_unique AS IsUnique, i.is_primary_key AS IsPrimaryKey, i.is_unique_constraint AS IsUniqueConstraint
FROM sys.indexes i
WHERE i.object_id = @ObjectId AND i.is_hypothetical = 0
