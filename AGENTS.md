# Instrucoes para IAs - PDV Local

## Regra principal
- Este diretorio e exclusivo do PDV local.
- Nao editar arquivos do ERP fora deste diretorio.

## Fonte de verdade de integracao
- Contratos de API e eventos: `../sync`.
- Nao inventar endpoints/fields sem atualizar o contrato.

## Runbook de desenvolvimento
- Antes de executar build, deploy em VM, homologacao ou testes remotos, leia
  `docs/development-structure-and-vm-runbook.md`.
- A VM de homologacao e acessada por WinRM. Nao gravar senha real no repositorio;
  usar variavel local `SYNC_VM_PASSWORD` ou cofre de credenciais.
- Para validar o ERP, usar o Python do ambiente virtual:
  `D:\GitHub\erp\venv\Scripts\python.exe`.

## Skills reutilizaveis
- Shared skills: `../skills-shared/skills` (modo `shared-live`).
- Skills locais: `./skills`.

## Efeito de alteracao de skill
- Alteracao em `../skills-shared/skills` vale para ERP, PDV e Sync na proxima sessao do agente.
- Para congelar comportamento em release, usar snapshot local em `./skills/vendor/<versao>`.

## Boas praticas obrigatorias
1. Manter compatibilidade com a versao de contrato ativa.
2. Criar testes para qualquer mudanca de comunicacao.
3. Evitar acoplamento com implementacao interna do ERP.

