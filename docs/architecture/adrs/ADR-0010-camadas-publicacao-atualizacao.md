# ADR-0010: Camadas, publicação, atualização, rollback e fallback — implementação

- **Status**: aceito
- **Data**: 2026-09-30
- **Fase**: 7 — Camadas, versões e publicação

## Contexto

ADR-0006 fixou o formato (overrides por caminho lógico, proveniência no
snapshot, rollback por ponteiro). A Fase 7 precisa torná-lo executável com
as regras da seção 16 (base/produto protegidos, cliente só com diferenças,
políticas manual e automática, "falha de atualização nunca substitui a
versão ativa") e da seção 17 (fallback por lista ordenada). Várias escolhas
concretas ficaram em aberto.

## Decisão

1. **Caminho lógico = caminho do editor** (`/DPS/numero`,
   `/Pedido/itens/item`), sem índice. `MappingPaths` converte
   `MappingTemplate` ⇄ mapa por caminho usando a árvore de destino
   (`DestinationTreeBuilder`, a mesma do editor): complexo obrigatório
   sempre emitido, bloco repetível só com coleção, conteúdo não mapeável
   ignorado.
2. **Camadas imutáveis e append-only** (`templateLayers`): versão
   sequencial por `(organização, camada)`. Base: XSD de destino + conjunto
   completo (só `Set`), sem pai. Produto: pai base. Cliente: pai produto ou
   base. Produto/cliente não trazem XSD. Tipo da camada não muda entre
   versões. Tabelas DE-PARA também por override (define/remove a tabela).
3. **Proteção e multiempresa**: camada de outra organização só pode ser pai
   (ou topo de um vínculo) se estiver marcada `Shared`; nenhuma operação
   altera camada existente — "base e produto protegidos" é garantido pelo
   modelo (imutável) e pela regra de pai; a autorização por ação vem na
   Fase 9. A mesma regra vale para **toda** cadeia avaliada — criação,
   publicação, propagação e rebase (`LAYER_ACCESS_DENIED`): o topo é da
   organização do template ou compartilhado, e cada pai é do mesmo dono do
   filho ou compartilhado. `Shared` é por versão: uma versão nova não
   compartilhada não chega a clientes de outra organização.
4. **Resolução explicável** (`LayerResolver`): aplica base → produto →
   cliente, valida cada override contra a árvore de destino do XSD da base e
   grava, por caminho e por tabela, a história completa
   (`RuleTrace`: camada, tipo, operação, comentário) e a fonte final. Vai no
   manifesto do snapshot junto com as camadas exatas (ref + hash). Conflitos:
   caminho fora do XSD, nó não mapeável e tipo de regra incompatível são
   **críticos** (bloqueiam); remoção de algo inexistente e regra dentro de
   bloco sem coleção são **avisos**.
5. **Publicação por camadas** (`LayeredTemplateService`): resolve →
   conflitos críticos bloqueiam → compila → **gates** (`IPublicationGate`:
   compilação + cenários funcionais do vínculo; a Fase 8 acrescenta os
   estruturais) → publica com o manifesto (que entra no hash: mesma saída
   por outra combinação de camadas vira versão nova e rastreável).
   Qualquer falha é devolvida e auditada; nada é gravado no snapshot.
6. **Vínculo** (`templateBindings`): chave de runtime → política
   (`Manual`/`Automatic`), cenários funcionais e linhagem (camadas da cadeia
   ativa, sem versão, indexada). Gravado só quando a publicação passa.
7. **Propagação**: nova versão de camada afeta os vínculos cuja cadeia
   ativa a tem **no topo ou logo abaixo do topo**. Se for o pai direto do
   topo, o topo é **rebaseado** (mesmas diferenças, novo pai) — só se o
   topo for da organização do vínculo. Camada mais funda (ex.: base sob um
   produto de outro dono) não rebaseia camada alheia: o dono do produto
   publica o produto novo, que então propaga. Automática: avalia com o topo
   rebaseado em memória; só se tudo passar grava a nova versão do topo e
   publica. Manual: grava proposta com diferenças (`ResolutionDiff`:
   adicionado/removido/alterado e camada de origem antes/depois — mudança
   só de versão da mesma camada não conta), conflitos e resultado dos gates;
   a aprovação reavalia com o estado atual; nova proposta substitui a
   pendente. Toda tentativa fica em `templateUpdates`. Se a camada nova é o
   **próprio topo**, vale a cadeia que ela declara (o cliente pode escrever
   a versão nova já sobre o produto novo). Cada vínculo é processado
   isoladamente: erro inesperado vira atualização `Failed`
   (`UPDATE_UNEXPECTED_ERROR`) sem interromper os demais; vínculo cuja
   versão ativa foi publicada sem camadas (publicador direto das Fases 5–6)
   registra `ACTIVE_WITHOUT_LAYERS` em vez de ser ignorado em silêncio.
   Quem cria a camada só recebe de volta as atualizações da própria
   organização; as demais ficam no histórico de cada template.
7a. **Concorrência otimista**: toda publicação por camadas é condicionada
   à versão ativa que foi avaliada (`SnapshotDraft.EnforceExpectedActive`/
   `ExpectedActiveVersion`, verificada dentro da transação do MongoDB). Se
   outra publicação/rollback chegou antes, nada é gravado
   (`PUBLICATION_CONCURRENT_CHANGE`); a propagação automática reavalia
   sobre o novo estado (até 3 vezes) — se ele já tem a camada em versão
   igual ou mais nova, não há o que fazer. Resultado: uma propagação mais
   velha nunca sobrescreve uma mais nova.
7b. **Propostas só valem sobre a versão em que foram feitas**: publicação,
   vínculo e rollback marcam as propostas pendentes como `Superseded`; a
   aprovação confere se a versão ativa ainda é a da proposta — se não for,
   marca `Superseded` e recusa, sem publicar.
8. **Rollback**: reativa uma versão já publicada (a informada ou a maior
   abaixo da ativa) numa transação que rebaixa a ativa — sem re-resolver
   camadas; o Change Stream (filtro `status = active`) atualiza os caches.
9. **Fallback de execução** (seção 17): `candidateVersions` (API: `versions`)
   em ordem; a primeira que gera XML válido é usada; se todas falharem, o
   resultado traz as tentativas com os diagnósticos de cada versão. É
   fallback local (gera e valida aqui), não descoberta do que a prefeitura
   aceita.
10. **Auditoria** (`auditEvents`): criação de camada, publicação,
    configuração de vínculo, proposta/aplicação/rejeição de atualização e
    rollback — ator, alvo, resultado e detalhes estruturais (cadeia,
    versões, códigos de falha), **nunca** payload fiscal.
11. **Escrita só por SDK/Application nesta fase** (`TraduxAdministration`):
    a API ganha apenas leitura (`.../versions/{v}/explanation`,
    `.../updates`). Escrita por HTTP e publicação pelo editor chegam com a
    autorização da Fase 9 (ROADMAP ajustado).

## Alternativas consideradas

- **Rebasear em cascata camadas de outros donos** (base nova → produto do
  fornecedor rebaseado automaticamente): violaria "base e produto
  protegidos" — o dono do produto decide quando acompanhar a base.
  Rejeitada.
- **Gravar a camada rebaseada antes de avaliar**: deixaria versões órfãs a
  cada atualização reprovada. Rejeitada: avalia em memória, grava só se
  passar. Resta uma janela: a versão rebaseada é gravada imediatamente
  antes da publicação; se a publicação então perde a corrida de
  concorrência (ou a infraestrutura falha), essa versão fica sem uso.
  Aceito: camadas são imutáveis, a versão órfã é inofensiva (nenhum
  snapshot a referencia) e rastreável (comentário "Rebase de … sobre …" e
  evento de auditoria).
- **Endpoints HTTP de escrita já nesta fase**: sem autorização por ação
  (Fase 9) qualquer chamador publicaria/rollback. Rejeitada.
- **Rollback re-resolvendo camadas antigas**: desnecessário e arriscado —
  o snapshot publicado é imutável e completo.

## Consequências

- O gate "atualização incompatível não altera a versão ativa" é garantido
  por construção (nada é escrito antes de todos os gates passarem) e
  coberto por testes com conflito de XSD e com cenário reprovado.
- Snapshots publicados sem camadas (Fases 5–6) continuam válidos; têm
  manifesto nulo (explicação vazia).
- A propagação lê vínculos de qualquer organização (`FindByLineageAsync`):
  é um processo interno; a Fase 9 precisa garantir que nenhum chamador de
  outra organização o dispare ou leia seu resultado.
- Os gates estruturais (Fase 8) entram pela mesma interface, sem mudar o
  fluxo.
- Mensagens dos gates (gravadas em atualizações e auditoria) trazem só
  código e XPath, nunca valores do cenário ou do XML gerado.
- Os repositórios MongoDB da Fase 7 criam seus índices antes da primeira
  escrita (não dependem do startup da API) — um host do SDK tem as mesmas
  garantias de unicidade.
- Cadeia declarada por uma camada nova que rebaixa uma camada ativa
  (`LAYER_DOWNGRADE`) nunca é aplicada automaticamente: vira proposta.
- Versões criadas por rebase são gravadas direto (sem nova propagação): um
  rebase feito para um vínculo não notifica outros vínculos que usem a
  mesma camada; cada vínculo rebaseia a sua. Dívida conhecida: o
  publicador direto do SDK (`TraduxRuntime.PublishAsync`) não passa
  pelos gates — fechado na Fase 8; rollback sem concorrência condicionada
  e status de proposta sem transição condicional — Fase 9.
