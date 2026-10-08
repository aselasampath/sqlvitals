DECLARE @periodTo   datetimeoffset = ISNULL(@toUtc,   SYSDATETIMEOFFSET());
DECLARE @periodFrom datetimeoffset = ISNULL(@fromUtc, DATEADD(MINUTE, -@minutes, @periodTo));
