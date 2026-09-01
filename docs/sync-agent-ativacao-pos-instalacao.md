# Sync Agent - Ativacao Pos-Instalacao

Data: 01/06/2026

## Objetivo

Definir a nova fase funcional em que o cliente instala o SyncAgent e, depois
da instalacao, ativa a conexao com o ERP pelo dashboard/tray local.

Esta fase substitui o fluxo puramente tecnico em que `InstanceId`,
`ErpTenantId`, `ErpApiBaseUrl` e `AccessToken` precisam ser informados
diretamente no instalador.

## Estado atual concluido

Base tecnica ja entregue:

- SyncAgent .NET Worker criado em `src/sync-agent`;
- SyncAgent.Tray criado em `src/sync-agent-tray`;
- banco local PostgreSQL 17 com pgvector 0.8.0;
- timezone local do PostgreSQL definido como `America/Sao_Paulo`;
- schema local `sync_agent`;
- outbox, dispatcher, retry, dead-letter, heartbeat e reconciliacao;
- dashboard local em `http://127.0.0.1:47891/`;
- ajuda local em `/help`;
- logs operacionais em `/logs`;
- amostra de registros alterados por operacao no dashboard de logs;
- instalador Windows com pacote em `artifacts/sync-agent-installer`;
- validacao de pacote por `install-sync-agent.ps1 -ValidateOnly`;
- segredo Arpa protegido por DPAPI `LocalMachine`;
- piloto Anapolis validado com dados reais do Arpa:
  - 1.538 produtos;
  - 2.136 clientes;
  - 3.674 eventos aceitos pelo ERP;
  - fila `0`;
  - dead-letter `0`;
  - reconciliacao remota `matched=true`.

Seguranca ja decidida:

- o SyncAgent nunca grava dados no banco Arpa;
- fluxo permitido: `Arpa -> SyncAgent -> ERP`;
- fluxos proibidos:
  - `SyncAgent -> Arpa`;
  - `ERP -> SyncAgent -> Arpa`;
- usuario runtime do Arpa deve ser estritamente read-only;
- senha do Arpa deve ficar fora do `appsettings.json`;
- em Windows, preferir `ArpaCollector:PasswordProtectedFile` com DPAPI.

## Problema da proxima fase

Hoje o SyncAgent ja funciona, mas a conexao com o ERP ainda depende de
provisionamento tecnico no instalador:

```powershell
-InstanceId "<instance>"
-ErpTenantId "<tenant>"
-ErpApiBaseUrl "<url-erp>"
-AccessToken "<token>"
```

Para uso pelo cliente final, isso deve virar uma ativacao pos-instalacao:

1. cliente instala o SyncAgent;
2. dashboard/tray abre em modo nao provisionado;
3. cliente informa URL do ERP e codigo de ativacao;
4. SyncAgent valida a ativacao com o ERP;
5. ERP emite credenciais tecnicas da instalacao;
6. SyncAgent salva as credenciais com protecao do Windows;
7. sincronizacao inicia automaticamente.

## Decisao rigida

O SyncAgent nao deve armazenar usuario e senha do ERP.

Usuario/senha do ERP, quando usados, servem apenas para gerar ou autorizar um
codigo de ativacao no ERP. Depois da ativacao, o agente usa credenciais
tecnicas proprias da instalacao:

```text
InstanceId
ErpTenantId
AccessToken tecnico
certificado mTLS por instalacao, futuramente
```

## Estados oficiais planejados

```text
not_provisioned
provisioning
provisioned
provision_failed
degraded
disabled
```

Enquanto estiver `not_provisioned`, o agente deve:

- nao coletar do Arpa;
- nao enviar eventos ao ERP;
- nao executar dispatcher;
- nao executar heartbeat remoto;
- nao executar reconciliacao remota;
- expor apenas dashboard/setup local.

## Contratos planejados

Antes de implementar ERP ou SyncAgent, o repositorio `../sync` deve receber o
contrato oficial.

Endpoints previstos:

```text
POST /v1/sync/activation:validate
POST /v1/sync/activation:complete
POST /v1/sync/agents/{instanceId}/token:refresh
```

Requisitos do codigo de ativacao:

- vinculado a um tenant;
- validade curta;
- uso unico;
- revogavel;
- auditado com usuario que gerou;
- nao deve aparecer em logs.

## Tela local planejada

Endpoints locais previstos no SyncAgent:

```text
GET  http://127.0.0.1:47891/setup
POST http://127.0.0.1:47891/setup/activate
```

Campos iniciais:

```text
URL do ERP
Codigo de ativacao
Botao Conectar
Status da ativacao
```

Depois de ativado, `/setup` nao deve permitir nova ativacao comum. Reprovisionar
uma instalacao deve exigir acao administrativa explicita.

## Gates da fase

Status em 01/06/2026:

- Gate 1 - Contrato: concluido no repo `../sync`, contrato `1.4.0`.
- Gate 2 - ERP: concluido tecnicamente no app `sync_api`.
- Gate 3 - SyncAgent: concluido tecnicamente no app `src/sync-agent`.
- Gate 4 - Seguranca: concluido tecnicamente com validacao integrada local.
- Gate 5 - Homologacao: concluido tecnicamente no piloto Anapolis.

### Gate 1 - Contrato

- Atualizar OpenAPI no `../sync`.
- Criar exemplos oficiais de request/response.
- Atualizar changelog de contrato.
- Definir erros padronizados:
  - codigo expirado;
  - codigo ja usado;
  - tenant invalido;
  - ERP indisponivel;
  - instalacao revogada.

### Gate 2 - ERP

- Criar modelo/registro de codigo de ativacao.
- Criar emissao de codigo pelo ERP.
- Validar codigo de uso unico.
- Emitir credenciais tecnicas da instalacao.
- Auditar criacao, uso, falha e revogacao.
- Testar ativacao feliz, expiracao, reuso e revogacao.

### Gate 3 - SyncAgent

- Suportar estado `not_provisioned`.
- Bloquear sincronizacao antes da ativacao.
- Criar `/setup`.
- Implementar chamada de ativacao ao ERP.
- Persistir credenciais tecnicas com protecao do Windows.
- Atualizar dashboard/tray para mostrar estado de ativacao.

### Gate 4 - Seguranca

- TLS obrigatorio fora de `localhost`.
- mTLS por instalacao como requisito de producao.
- Access token tecnico sem usuario/senha do ERP.
- Segredos em cofre/protecao do sistema operacional.
- Logs estruturados sem token, codigo de ativacao, senha ou payload sensivel.
- Falhas de ativacao auditaveis.

### Gate 5 - Homologacao

- Instalar pacote em maquina limpa como Administrador.
- Abrir dashboard/tray em estado `not_provisioned`.
- Ativar com codigo real gerado pelo ERP.
- Reiniciar servico/Windows e confirmar que segue `provisioned`.
- Confirmar inicio da sincronizacao apos ativacao.
- Confirmar `dead_letter=0`, fila controlada e reconciliacao `matched=true`.

## Criterios de nao inicio

Nao implementar ERP ou SyncAgent antes de:

- contrato estar definido no `../sync`;
- exemplos oficiais estarem criados;
- decisao de validade e uso unico do codigo estar fechada;
- decisao de armazenamento local das credenciais estar fechada;
- fluxo de revogacao estar documentado.

## Gate 1 concluido

Repositorio: `../sync`

Entregue:

- contrato atualizado para `1.4.0`;
- endpoints adicionados:
  - `POST /v1/sync/activation:validate`;
  - `POST /v1/sync/activation:complete`;
  - `POST /v1/sync/agents/{instanceId}/token:refresh`;
- exemplos oficiais adicionados para validate, complete e refresh;
- changelog atualizado;
- compatibilidade registrada como non-breaking.

## Gate 2 concluido

Repositorio: `../erp`

Entregue:

- modelo `SyncActivationCode`;
- campos de token curto/refresh token em `SyncInstallation`;
- comando `create_sync_activation_code`;
- endpoints:
  - `POST /v1/sync/activation:validate`;
  - `POST /v1/sync/activation:complete`;
  - `POST /v1/sync/agents/{instanceId}/token:refresh`;
- access token tecnico com expiracao;
- refresh token tecnico armazenado apenas como hash;
- codigo de ativacao armazenado apenas como hash;
- codigo de ativacao com expiracao, uso unico e revogacao;
- admin atualizado para auditoria;
- documentacao do ERP atualizada.

Validado:

- `python manage.py test sync_api --keepdb`: 28 testes OK;
- `python manage.py makemigrations sync_api --check --dry-run`: sem pendencias;
- `python manage.py check`: OK com aviso conhecido do CKEditor;
- `python manage.py migrate sync_api`: migration `0003` aplicada;
- smoke local de `POST /v1/sync/activation:validate`: OK;
- codigo temporario de smoke revogado e arquivo local removido.

## Gate 3 concluido

Repositorio: `../pdv-local`

Entregue:

- configuracao `Provisioning`;
- estado runtime `not_provisioned`;
- bloqueio de collector, dispatcher, heartbeat e reconciliacao enquanto a
  instalacao nao estiver ativada;
- tela local `GET /setup`;
- acao local `POST /setup/activate`;
- cliente de ativacao usando os contratos:
  - `POST /v1/sync/activation:validate`;
  - `POST /v1/sync/activation:complete`;
  - `POST /v1/sync/agents/{instanceId}/token:refresh`;
- persistencia das credenciais tecnicas em arquivo protegido por DPAPI
  `LocalMachine`;
- refresh automatico do access token tecnico antes do vencimento;
- dashboard e `/status` exibindo:
  - `provisioning_enabled`;
  - `provisioned`;
  - `instance_id` efetivo;
  - `erp_tenant_id` efetivo;
  - `erp_api_base_url` efetivo;
- instalador Windows com modo `-EnablePostInstallActivation`.

Configuracao principal:

```json
{
  "Provisioning": {
    "Enabled": true,
    "ProtectedFile": "C:\\Program Files\\AraraSuite.com.br\\Sync\\Secrets\\sync-agent-provisioning.dpapi",
    "ActivationTimeoutSeconds": 30
  }
}
```

Use caminho absoluto em `Provisioning:ProtectedFile` no servico instalado. Em
desenvolvimento, caminhos relativos podem ser resolvidos pelo diretorio base do
projeto executado por `dotnet run`.

Validado:

- `dotnet build src\sync-agent\SyncAgent.csproj --no-incremental`: OK;
- smoke local com `Provisioning.Enabled=true`:
  - antes da ativacao: `runtime_status=not_provisioned`;
  - `/setup`: HTTP 200;
  - `/setup/activate`: HTTP 200;
  - depois da ativacao: `runtime_status=idle`;
  - depois da ativacao: `provisioned=true`;
  - credencial DPAPI criada e removida ao fim do smoke.

## Gate 4 concluido

Repositorio: `../pdv-local`

Entregue:

- ativacao valida HTTPS obrigatório fora de `localhost` e `127.0.0.1`;
- URL de API retornada pelo ERP durante ativacao tambem passa pela validacao
  de HTTPS/local antes de ser persistida;
- refresh de token tecnico aplica a mesma regra de HTTPS/local;
- dashboard/API local adiciona headers:
  - `Cache-Control: no-store`;
  - `Pragma: no-cache`;
  - `X-Content-Type-Options: nosniff`;
- formulario de ativacao limitado a 8 KiB;
- credenciais tecnicas continuam armazenadas apenas em DPAPI `LocalMachine`;
- SyncAgent continua sem armazenar usuario/senha do ERP;
- fluxo de dados permanece somente `Arpa -> SyncAgent -> ERP`;
- enforcement de mTLS ja existe por `ErpSecurity:RequireMutualTls=true` fora
  de ambiente local; certificado real por instalacao deve ser validado na
  homologacao.

Validado:

- `dotnet build src\sync-agent\SyncAgent.csproj -c Release -o artifacts\build-validation\sync-agent`: OK;
- smoke de ativacao em build isolado:
  - antes da ativacao: `runtime_status=not_provisioned`;
  - `/setup`: HTTP 200 com `Cache-Control: no-store`;
  - URL HTTP remota: HTTP 400;
  - `/setup/activate` em `http://127.0.0.1:8000`: HTTP 200;
  - depois da ativacao: `runtime_status=idle`;
  - depois da ativacao: `provisioned=true`;
- pacote recriado em `artifacts/sync-agent-installer`;
- `install-sync-agent.ps1 -ValidateOnly`: OK;
- piloto Anapolis apos Gate 4:
  - ERP online;
  - fila local `0`;
  - dead-letter `0`;
  - reconciliacao remota `matched=true`;
  - `/logs` HTTP 200.

## Gate 5 concluido

Repositorio: `../pdv-local`

Entregue:

- script de homologacao tecnica:
  `infra/windows/test-sync-agent-release-readiness.ps1`;
- geracao de evidencia JSON sem expor token;
- validacao do pacote por `install-sync-agent.ps1 -ValidateOnly`;
- validacao do contrato `../sync` em `1.4.0`;
- validacao do dashboard local, `/logs`, `/setup` e `/sync-now`;
- validacao de fila, dead-letter, runtime e estado provisionado;
- validacao do status central no ERP;
- preflight read-only do Arpa;
- preflight das views `sync_export`;
- registro do bloqueio real encontrado e corrigido:
  `permission denied for relation produtos`.

Validado em 02/06/2026:

- `test-sync-agent-release-readiness.ps1`: OK;
- evidencia:
  `artifacts/sync-agent-release-readiness-anapolis.json`;
- contrato `1.4.0`: OK;
- pacote Windows: OK;
- runtime local: `idle`;
- instalacao: `provisioned=true`;
- fila local: `0`;
- dead-letter local: `0`;
- `/logs`: HTTP 200;
- `/setup`: `Cache-Control=no-store`;
- ERP: `connectivity=online`;
- ERP queue: `0`;
- ERP rejected 24h: `0`;
- reconciliacao ERP: `matched=true`;
- permissoes Arpa read-only: OK;
- exportacao Arpa: OK.

Bloqueio encontrado durante o Gate 5:

- o runtime falhou com `42501: permission denied for relation produtos`;
- a correcao foi reaplicar a preparacao controlada do Arpa Anapolis com DBA
  `postgres` sem senha, apenas para DDL/grants:
  - recriacao/garantia das views `sync_export`;
  - `GRANT SELECT` para `sync_agent_anapolis_ro`;
  - revogacao de permissoes de escrita;
  - sem escrita em dados de negocio do Arpa.

Pendencias externas para release com usuario final:

- executar instalacao em maquina limpa com PowerShell elevado;
- validar reinicio real do Windows/servico instalado;
- definir canal MSI/EXE assinado;
- validar certificado mTLS real por instalacao quando o gateway final estiver
  disponivel.

Faltam 0 gates tecnicos para concluir a fase de ativacao pos-instalacao.

## Proxima fase

Fase 2 - Homologacao externa / instalacao limpa.

Documento operacional:

```text
docs/sync-agent-homologacao-externa.md
```

Script de evidencia pos-instalacao:

```text
infra/windows/test-sync-agent-clean-install.ps1
```

Objetivo:

- instalar o pacote em maquina Windows limpa;
- ativar pelo dashboard local `/setup`;
- validar servico, banco, dashboard, logs, fila, dead-letter e ERP;
- reiniciar servico/Windows;
- testar rollback preservando banco local.

## Melhorias na tela `/setup` (01/09/2026)

Para reduzir retrabalho na ativacao:

- **URL do ERP fica salva.** Depois do primeiro envio, a URL informada e
  guardada em `Provisioning:SetupHintFile` (padrao:
  `.secrets/sync-agent/setup-hint.json`) e tambem em memoria, e volta
  pre-preenchida no formulario mesmo apos reiniciar o servico. A URL nao e
  segredo (hostname publico), entao fica em texto puro.
- **Codigo de ativacao persiste entre tentativas na mesma sessao.** Se a
  ativacao falha por um motivo corrigivel (URL errada, ERP fora do ar, timeout),
  o codigo continua no campo para o operador so ajustar e reenviar. Ele e
  descartado no sucesso e nos erros terminais de codigo (`activation_code_used`,
  `_expired`, `_revoked`, `_not_found`, `already_provisioned`). O codigo **nunca**
  e gravado em disco.
- **Erro de rede nao vira mais HTTP 500.** `ErpActivationClient` trata
  `HttpRequestException` e timeout, devolvendo `erp_unreachable` /
  `activation_timeout` com mensagem legivel na propria tela.
- **Feedback mais claro.** A tela mostra o `code` do erro, uma dica por tipo,
  a linha "Estado atual" (aguardando ativacao / ativado / reconexao necessaria)
  e, no sucesso, a caixa verde "Ativacao aceita pelo ERP" alem do painel com
  instancia/tenant/URL/expiracao do token.
