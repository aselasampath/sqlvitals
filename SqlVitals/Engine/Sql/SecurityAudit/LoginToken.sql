SELECT sid AS Sid
FROM sys.login_token
WHERE usage = N'GRANT OR DENY' AND sid IS NOT NULL
