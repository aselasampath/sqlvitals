-- Without VIEW DEFINITION, sys.tables quietly lists only the tables the login has a permission on.
SELECT HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW DEFINITION') AS SeesAllTables
