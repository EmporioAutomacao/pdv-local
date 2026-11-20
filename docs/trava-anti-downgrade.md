# Trava anti-downgrade do SyncAgent/PDV

Nenhum caminho instala uma versão anterior à instalada. Quando não dá para comparar, recusa. Visão completa: `docs/registry/trava-anti-downgrade.md` no repositório `cp`.

| Onde | O que faz |
|---|---|
| `AgentVersion.IsDowngradeOrSame` | Falha fechada: versão que não dá para ler (como `0.1.0-dev`) não é tratada como atualização. |
| `SelfUpdater` | "Atualizar App" e o comando `pending_update` recusam versão igual ou menor. |
| Instalador (`InstallVersionComparer`) | Wizard desabilita o botão e `--auto` sai com código `6`. |
| `self-update.ps1` | Recusa no início, antes de parar qualquer coisa, e religa o serviço. |

## Regras do instalador

- A versão instalada é o maior número entre `Sync\Agent\VERSION` e o `ProductVersion` do `SyncAgent.exe`, porque qualquer um dos dois pode atrasar.
- A versão do pacote é o `VERSION` dele e, na falta, o `ProductVersion`.
- Sem número de um dos lados, o pacote é recusado.
- O mesmo commit continua permitindo reparo.

## O que continua igual

O rollback do `self-update.ps1` continua: ele desfaz uma atualização que não chegou a subir.

## Limites

- O `self-update.ps1` que roda é o do pacote: um pacote antigo traz o script antigo. A barreira nesse caso é o instalador e o agente já instalados.
- Um instalador publicado antes desta versão não tem a trava e, executado à mão como Administrador, ainda instala por cima. Um SyncAgent que reporta versão menor gera erro no log do ERP.
