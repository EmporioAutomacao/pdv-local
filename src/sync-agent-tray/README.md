# Sync Agent Tray

App de bandeja do Windows para diagnostico operacional do Sync Agent.

Status tecnico consolidado: `docs/sync-agent-sprint-1-status.md`.

## Funcao

- Consultar `GET http://127.0.0.1:47891/status`.
- Exibir status, tenant, instance id e pendencias no menu de bandeja.
- Acionar `POST http://127.0.0.1:47891/sync-now`.
- Copiar o ID da instalacao.

O Tray nao executa sincronizacao diretamente. Ele conversa com o `SyncAgent`,
que deve estar rodando como console em desenvolvimento ou como Windows Service
em producao.

## Executar em desenvolvimento

Inicie primeiro o Sync Agent:

```powershell
dotnet run --project src/sync-agent/SyncAgent.csproj
```

Depois execute o Tray:

```powershell
dotnet run --project src/sync-agent-tray/SyncAgent.Tray.csproj
```
