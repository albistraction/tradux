# Handoff atual

## Estado

Fase 22 (ADR-0025: o produto é só o microsserviço) commitada em 2026-11-16 (99b27ab).
Em 2026-11-17/18 o usuário instalou a skill `untangle-codebase` e pediu para rodá-la, com os extras
"Comments" e "Tests". Foram 4 ondas, todas commitadas:

- e32211e: saem a entrada por objeto C# e os `GenerationEvents` (decisão do usuário).
  `CacheRefreshingSnapshotRepository` atualiza o cache em toda gravação. Os handlers HTTP
  (`TemplateRequests`) são compartilhados com as rotas `/v1/organizations/…`, que ficaram
  (decisão do usuário). `tests/Shared/Drivers` saiu. O editor foi dividido.
- 3d5a31e: Xsd e motor sem sobras. Importação só em memória. `ScalarValues` (os operadores
  deixam de aceitar separador de milhar). `SignedByCaller`.
- e2da5d9: núcleo único de publicação (compila uma vez). Um só leitor de XML de origem.
  `IndexGuard` único. Opções tipadas. Limite de corpo por rota.
- onda 4: entrada JSON com limites configurados até os gates. A regressão lista o cenário não
  comparado. A auditoria do rascunho fica no serviço. `DuplicateKey` único.

Decisões: ADR-0025 §8 e DECISIONS (2026-11-17). O log da refatoração ficou no scratchpad da
sessão, que não é durável. O essencial está no ADR e nas mensagens de commit.

Verificado em 2026-11-18:
- build com 0 avisos;
- `dotnet test` 554/554;
- `node --test` 38/38;
- e2e `golden-path.mjs` GATE OK, depois da onda 4.

Em 2026-11-19 o projeto foi renomeado para **Tradux** (ADR-0026). Os renomes:
- `Nfse.Mapping.*` → `Tradux.*`;
- `Tradux.slnx`;
- configuração `Tradux:`;
- variáveis `TRADUX_*`;
- credencial `tradux_`;
- banco padrão `tradux-mapping`;
- pacote `tradux-package/1`.

Build 0 avisos, `dotnet test` 554/554, e2e GATE OK.
A base de dev foi copiada para `tradux-mapping`; o banco `nfse-mapping` ficou intacto e pode ser
apagado quando o usuário quiser.
O `<pasta-dev>\iniciar.ps1` já aponta para `src/Tradux.{Api,Web}`.

Repositório público: `https://github.com/albistraction/tradux` (branch `main`, commit único).
A documentação foi neutralizada antes de publicar: ERP genérico e caminhos locais como `<pasta-dev>`.
Não versione nome de cliente, caminho da máquina nem fonte de terceiros.

## Próxima tarefa exata

0. Apagar a pasta vazia e não versionada `tests/Nfse.Mapping.Web.Tests`. Ela estava travada
   por algum processo do usuário.
1. Revisão independente (agente `reviewer`) da Fase 22 + untangle, que é o gate item 6.
2. Se houver Docker: `docker compose -f deploy/docker-compose.yml --env-file deploy/.env up --build`.
3. Perguntar ao usuário se limpa os templates `pedido-e2e-*`/`nota-erp-e2e-*`/`nota-erp-ext-*`
   da base de dev.
4. Decisões pendentes, que mudam comportamento ou contrato:
   - `DraftTestRequest.Data` obrigatório;
   - editor só abre a operação `default`;
   - credenciais por HTTP;
   - captura diagnóstica no `/convert`.

## Como retomar

- MongoDB de dev: `pwsh -NoProfile -File <pasta-dev>\iniciar.ps1`, que sobe também
  a API (:5220) e o editor (:5230).
- Credencial dev: `tradux_dev-admin-org-a`.
- e2e: `node tests/Tradux.Web.Tests/e2e/golden-path.mjs`. Ele grava templates na base de dev.
- Dívidas: `docs/PROJECT_STATE.md` → "Dívida técnica aceita".
