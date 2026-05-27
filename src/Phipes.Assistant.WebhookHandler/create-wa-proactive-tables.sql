-- =============================================================================
-- Fase 2 — aviso proactivo de WhatsApp.
-- Crea las dos tablas que usa el BackgroundService WaProactiveNotifier en la base
-- PhipesAssistant (MSSQL local; Windows auth con la cuenta del app pool del handler).
--
-- NO se ejecuta automaticamente desde el codigo. Felipe lo corre una vez a mano
-- (sqlcmd / SSMS) sobre la base PhipesAssistant.
-- =============================================================================

USE [PhipesAssistant];
GO

-- -----------------------------------------------------------------------------
-- WaActiveTopic: el "tema activo" — el contacto por el que Felipe acaba de
-- preguntar. La skill /wsp hace UPSERT por Jid (borra filas viejas del mismo Jid
-- e inserta una nueva). El notifier vigila estos Jids dentro de la ventana
-- ActiveWindowMinutes.
-- -----------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.WaActiveTopic', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.WaActiveTopic
    (
        Id          INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_WaActiveTopic PRIMARY KEY,
        Jid         NVARCHAR(128)     NOT NULL,
        ContactName NVARCHAR(256)     NULL,
        AskedAtUtc  DATETIME2         NOT NULL CONSTRAINT DF_WaActiveTopic_AskedAtUtc DEFAULT SYSUTCDATETIME()
    );

    -- Indice por Jid: el UPSERT de /wsp borra por Jid y el notifier filtra por
    -- ventana temporal pero agrupa por Jid. No es UNIQUE porque el UPSERT borra
    -- antes de insertar (no dependemos de la unicidad a nivel de constraint).
    CREATE INDEX IX_WaActiveTopic_Jid ON dbo.WaActiveTopic (Jid);

    -- Indice de apoyo para la query del notifier (filtra por AskedAtUtc reciente).
    CREATE INDEX IX_WaActiveTopic_AskedAtUtc ON dbo.WaActiveTopic (AskedAtUtc);
END
GO

-- -----------------------------------------------------------------------------
-- WaProactiveNotified: idempotencia. Que mensajes (por Jid + timestamp epoch del
-- mensaje) ya fueron avisados a Felipe. La PK compuesta (Jid, MsgTs) garantiza
-- que el mismo mensaje no se avise dos veces aunque el notifier lo lea en varios
-- ticks (el staging puede no haberse purgado todavia).
-- -----------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.WaProactiveNotified', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.WaProactiveNotified
    (
        Jid           NVARCHAR(128) NOT NULL,
        MsgTs         BIGINT        NOT NULL,
        NotifiedAtUtc DATETIME2     NOT NULL CONSTRAINT DF_WaProactiveNotified_NotifiedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_WaProactiveNotified PRIMARY KEY (Jid, MsgTs)
    );
END
GO
