# ADR-0017: Mudanças do XSD de destino e remapeamento guiado

- **Status**: aceito
- **Data**: 2026-10-01
- **Fase**: 14 — Mudanças do XSD de destino e remapeamento

## Contexto

O usuário pediu: "se eu fizer o DE-PARA 1.0 e subir o novo XSD da prefeitura,
vai aparecer o que mudou para o operador remapear? Mudou sequência de tag, tipo
etc."

Até a Fase 13, carregar um XSD de destino novo num rascunho já mapeado:
- trocava a árvore;
- **descartava em silêncio** os mapeamentos cujo caminho deixou de existir
  (`pruneMappings`);
- não mostrava nada sobre tipo, cardinalidade, restrições ou ordem.

Não havia como comparar o XSD de um template publicado com uma versão nova.

## Decisão

1. **Analisador puro na Application**: `SchemaChangeAnalyzer`, em
   `Tradux.Application.Design`, compara duas importações
   (`ImportedXsd`) e, opcionalmente, os caminhos mapeados. Não grava nada e é
   determinístico.
   - **Como casa os elementos.** Pelo caminho lógico relativo à raiz. Grupos
     sem elemento próprio (`sequence`/`choice`/`all` aninhados) não entram no
     caminho, mas tornam o elemento opcional (choice ou grupo opcional) ou
     repetível. Assim, uma raiz renomeada não derruba o resto.
   - **Movido.** O topo de uma subárvore que saiu casa com o topo de uma que
     entrou quando o nome e o tipo de nó são iguais e não há ambiguidade. Os
     filhos vão junto.
2. **Mudanças reportadas** (`SchemaChangeKind`):
   - elemento novo, removido ou movido;
   - valor ↔ bloco; tipo do valor;
   - obrigatório ↔ opcional; único ↔ repetível; `nillable`;
   - restrições (tamanho, enumeração, pattern, limites, dígitos);
   - valor padrão;
   - **ordem dos filhos (sequência de tags)**;
   - modelo de conteúdo ou mapeabilidade;
   - raiz; namespace.
3. **Impacto no mapeamento** (`SchemaChangeImpact`):
   - **Remap**: um mapeamento existente deixou de valer. Casos: o campo saiu,
     foi movido, virou bloco ou valor, um bloco mapeado passou a repetir (ou
     deixou de repetir com for each), ou o conteúdo passou a choice/all.
   - **Map**: campo obrigatório sem mapeamento (novo, que virou obrigatório
     ou que perdeu o valor padrão), num bloco que é sempre gerado.
   - **Review**: o mapeamento continua, mas pode não servir. Casos: o tipo
     mudou, as restrições ficaram mais rígidas, a cardinalidade de um campo
     mapeado mudou, ou um bloco passou a ser obrigatório.
   - **Info**: sem ação. A ordem das tags é Info porque o gerador já emite na
     ordem do XSD (seção 15). Também entram aqui campo ou bloco opcional novo,
     restrição afrouxada e namespace.
4. **Sugestões de remapeamento.** Para cada caminho mapeado que saiu, o
   analisador sugere até 3 campos do mesmo tipo de nó que ainda não estão
   mapeados. Os candidatos são o mesmo nome em outro lugar ou um nome
   parecido no bloco correspondente (Levenshtein ≥ 0,4). Nunca se procura na
   árvore inteira, para manter o custo limitado. O campo novo obrigatório
   sugerido traz a dica "provável substituto de 'X'".
5. **Limites**:
   - até 5.000 mudanças reportadas;
   - sugestões para até 500 caminhos removidos;
   - "tem mapeamento dentro" resolvido por um conjunto de prefixos, em O(1).
6. **Superfícies** (o mesmo analisador em todas; SDK e API são equivalentes):
   - `POST /v1/design/schema-changes` (`template.edit`): XSD atual, XSD novo e
     mapeamento opcional. É o que o editor usa.
   - `POST /v1/organizations/{org}/templates/{id}/operations/{op}/schema-changes`
     (`template.read`): XSD da versão ativa × XSD novo. Outra organização ou
     template sem versão ativa → 404.
   - SDK: `TraduxDesigner.CompareSchemas` e
     `TraduxRuntime.CompareSchemaWithActiveAsync`.
7. **Editor**:
   - **Comparação antes da troca.** Ao carregar um XSD de destino num
     rascunho que já tem um, o editor compara antes de trocar.
     - Os mapeamentos que continuam válidos ficam, e a raiz renomeada é
       acompanhada.
     - Os demais viram **pendentes de remapear**, com sugestões; inclusive os
       que estavam dentro de um bloco movido.
     - Nada some em silêncio.
   - **Painel "Mudanças do XSD"**:
     - resumo por impacto;
     - lista do que mudou;
     - para cada pendente: "Mover para <sugestão>", "Mover para o campo
       selecionado" ou "Descartar".
     - A árvore de destino marca os nós afetados.
   - **Publicação bloqueada enquanto houver pendente.** O relatório é salvo
     no rascunho e só fecha sem pendentes.

## Alternativas rejeitadas

- **Diff textual dos XSDs**: mostra comentários e formatação e não fala de
  caminhos nem do mapeamento.
- **Remapear automaticamente pela melhor sugestão**: renomes ambíguos
  levariam a erros fiscais silenciosos. O operador decide; a sugestão só
  agiliza.
- **Comparar só a árvore do editor (`DestinationNode`)**: ela não traz as
  restrições (facets), e o pedido inclui "tipo etc.". A comparação usa o
  modelo canônico do XSD.

## Consequências

- Uma nova versão de layout municipal vira uma lista objetiva: o que
  remapear, o que mapear, o que revisar e o que é só informativo. O teste e
  os gates de publicação (Fases 6 a 8) continuam sendo a prova final.
- O analisador não interpreta semântica fiscal: um campo renomeado com outro
  significado precisa do julgamento do operador.
- O contrato de entrada (origem, ADR-0016) não é comparado aqui. Mudança de
  XSD de origem continua coberta pelo versionamento do contrato.

## Adendo (2026-10-02): XSD gigante ou patológico não trava nem derruba a API

Medição: o custo é da compilação e do pré-processamento de XSD do .NET, não
do código do projeto. Dois efeitos:
- **Custo de CPU que explode:**
  - bloco com 10 mil elementos opcionais: ~21 s;
  - mil tipos reutilizando um grupo de mil: ~29 s;
  - 8.000 tipos × `attributeGroup` de 8.000: ~25 s e 5 GB.
- **Estouro de pilha**, que derruba o processo sem 400 nem 500:
  - aninhamento XML de mil níveis;
  - cadeias de alguns milhares de `simpleType`, `union`/`list`, extensão
    ou restrição, `attributeGroup` ou `group ref`;
  - tipo recursivo no destino.

O pacote nacional da NFS-e 1.01 (505 elementos) importa em 40 a 440 ms.

Defesas, todas antes de compilar e com recusa em `400`:
1. **`XmlDepthGuard`**: aninhamento XML até `MaxXmlDepth` (128), checado
   em todo arquivo carregado (em memória, ZIP ou disco).
2. **`ReferenceGraphGuard`** (primeira linha de defesa, barata): altura do
   grafo de referências até `MaxReferenceHeight` (256).
   - Arestas consideradas: tipo base (extensão e restrição, simple e complex),
     `list`/`union`, `group ref`, `attributeGroup ref`, `element ref`,
     tipo de elemento e de atributo, e `substitutionGroup`.
   - O cálculo é iterativo (pilha explícita). Em grafo sem ciclo, não depende
     da ordem de declaração nem do arquivo incluído.
   - Arestas de ciclo não somam (tipo recursivo legal na origem). Com ciclos,
     a altura pode ser subestimada (caminho mais longo é NP-difícil). Por isso
     o limite que vale para a árvore é o item 7.
3. **`ContentModelSizeChecker`**, com contagem memoizada e custo linear.
   Recusa quando há:
   - mais de `MaxElementsPerContentModel` (1.000) elementos num tipo;
   - soma acima de `MaxContentModelParticlesTotal` (20.000) elementos;
   - mais de `MaxAttributesPerType` (500) atributos expandidos num tipo;
   - soma acima de `MaxAttributeUsesTotal` (20.000) atributos;
   - mais de `MaxElementDeclarations` (10.000) declarados;
   - ciclo de `group`, `attributeGroup` ou tipo base;
   - `xs:redefine`.
   - Também limita:
     - membros expandidos de `union`/`list`: até `MaxUnionMembers` (64)
       por tipo e 20.000 no total. Entram tipos globais e anônimos (de
       elemento, de atributo e de `simpleContent`). A validação percorre os
       membros a cada valor;
     - namespaces em curingas `xs:any`/`xs:anyAttribute` (64 por curinga,
       2.000 no total);
     - XPath de `xs:key`/`xs:unique`/`xs:keyref`, avaliado em cada XML de
       entrada (limite das declarações; o custo real, que cresce com escopos
       aninhados, é limitado pelo orçamento de tempo do item 8):
       - 1.000 caracteres e 32 alternativas por XPath;
       - até 16 restrições;
       - soma de campos + seletores até 64.
     O pacote nacional não usa `union`, `anyAttribute` nem chaves.
4. **Erros de schema** do `Compile` não são mais engolidos.
5. **Tipo recursivo no XSD de destino** é recusado. Na origem continua
   aceito, com corte no 2º nível.
6. **Segunda proteção**: a importação roda numa thread com pilha de 64 MB
   (reservada, não alocada).
7. **Árvore expandida limitada ao ser montada** (`ExpandedTreeLimits`):
   - até 128 níveis de elementos e grupos, no destino e na origem;
   - até 20.000 nós no destino (a origem já tinha 20.000 campos).
   Tipos reutilizados multiplicam a árvore: a profundidade é altura ×
   aninhamento, e o número de nós é leque^altura. Limitar a árvore montada
   cobre qualquer combinação. Tudo que percorre a árvore depois (editor e
   comparação) também fica limitado, mesmo rodando na thread da requisição.
   Os testes estruturais geram dados sintéticos que crescem com coleções
   aninhadas e campos. Por isso têm orçamento próprio de nós (objetos +
   valores):
   - por caso: acima de 20.000, as coleções recebem só o mínimo; acima de
     100.000, mais nada é gerado;
   - na suíte: acima de 1 milhão, os casos seguintes saem vazios e reprovam;
   - o JSON sintético aceita até 1.024 níveis (antes, mais de 64 virava 500);
   - o que deixou de ser exercitado vira lacuna, e dado cortado reprova,
     nunca aprovação silenciosa.
8. **Prazo total por operação**, para custos que crescem com o documento:
   patterns logo abaixo do timeout, chaves em escopos aninhados e validação do
   .NET.
   - **Requisição de geração:** `XmlGenerationService.RequestBudget` (8 s),
     somadas todas as versões do fallback; as que não couberem viram
     `GENERATION_TIME_LIMIT`. Dentro dele:
     - leitura da origem: `SourceXmlLimits.MaxValidationTime` (3 s, ou o
       restante do pedido); para ao atingir 20 problemas reportados;
     - geração no motor: `GenerationDeadline.Budget` (5 s), com
       `XSD_VALIDATION_TIMEOUT`.
   - **Suíte estrutural:** `StructuralSuiteBuilder.MaxRunTime` (15 s) cobre
     perfil, valores sintéticos com pattern, plano e execução. O que não
     couber reprova (`STRUCTURAL_SUITE_TIME_LIMIT` ou valor indisponível).
   - **Como o prazo é levado:** pelo `GenerationDeadline` (`AsyncLocal`),
     que atravessa `await`. Escopos aninhados valem o menor prazo.
   - **Limite de uma leitura:** o prazo é checado entre leituras do
     validador, e uma leitura sozinha pode avaliar vários patterns. Por isso:
     - timeout por avaliação de regex: 250 ms (`RegexTimeout`,
       `PatternGuard`);
     - até 8 avaliações de pattern por valor (restrições encadeadas ×
       membros de union) e 8 por tag (atributos da mesma tag);
     - pior leitura: cerca de 4 s.
   - **Tempo de recusa:** os ataques de custo (patterns, chaves aninhadas)
     são recusados quando o prazo estoura, de 3 a 8 s. Os de estrutura do XSD
     são recusados na importação, em até ~300 ms.

   Custo residual aceito: um XSD de 31 KB dentro dos limites (300 tipos ×
   grupo de 64) importa em ~2 s.

   Os limites da árvore são constantes de `ExpandedTreeLimits` e não ficam em
   `XsdImportSecurityOptions`. `SnapshotCompiler` trata o tipo não suportado como erro de
   compilação, não como 500.

Configuração: os limites estão em `XsdImportSecurityOptions` e são
configuráveis **só por código** (SDK, `DraftDesignService`, `SnapshotCompiler`).
A API usa os padrões. `LayeredTemplateService` e `TraduxClient` importam
com os padrões.

Medido: os ataques de estrutura encontrados nas revisões são recusados na
importação em até ~300 ms. Os de custo de validação param no prazo da
operação. Nenhum deles derruba o processo nem passa do prazo. Um XML normal de
1 MB valida em ~0,15 s, longe dos 3 s.

Riscos aceitos:
- Um snapshot publicado antes destes limites que os ultrapasse deixa de
  compilar no aquecimento. Nenhum XSD fiscal real chega perto; se acontecer,
  é preciso republicar com o XSD ajustado.
- `substitutionGroup` longo (cadeia de 9 mil) leva ~3 s se ficar abaixo da
  altura máxima. Com o limite de 256, não chega a isso.

Na comparação, os caminhos mapeados por bloco removido passaram a ser achados
por busca binária.
