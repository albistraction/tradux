# ADR-0011: Qualidade automatizada — dados sintéticos, testes obrigatórios e gate único de publicação

- **Status**: aceito
- **Data**: 2026-09-30
- **Fase**: 8 — Qualidade automatizada

## Contexto

Seção 19 do requisito: gerar dados sintéticos a partir dos contratos e
cobrir obrigatórios, obrigatórios + opcionais, limites e tamanhos,
enumerações e patterns, repetições, cada ramo de `choice`, nulos permitidos
e casos inválidos esperados, sem explosão combinatória; exigir ao menos um
cenário funcional do autor (entrada + XML esperado ou asserções
estruturais); comparar XML semanticamente; nenhum template publicado com
teste obrigatório falhando. Gate da Fase 8: "não é possível publicar com
teste obrigatório falhando".

Restrições encontradas:

- O modelo interno do XSD (`SchemaElement`) não guardava facetas
  (tamanho, enumeração, pattern, limites, dígitos) — só o tipo escalar.
- O contrato de origem tipado não é persistido (ADR-0009): o template
  conhece os caminhos de origem que referencia, não os tipos deles.
- A revisão da Fase 7 (W1) apontou que `TraduxRuntime.PublishAsync`
  publica sem gate algum.
- O motor ainda não mapeia conteúdo de `xs:choice`/`xs:all` (ADR-0009).

## Decisão

1. **Facetas no modelo do XSD** (`ScalarFacets` em `SchemaElement`):
   extraídas da cadeia de restrições do tipo simples no `XmlSchemaSet`
   compilado — `length`/`minLength`/`maxLength`, enumeração, patterns (cada
   passo de derivação é um conjunto "OU"; passos combinam em "E"), limites
   inclusivos/exclusivos, `totalDigits`, `fractionDigits`. Aditivo: nada
   que já existia muda.
2. **Perfil de origem inferido do template** (`SourceProfiler`): percorre o
   mapeamento junto com o schema de destino e, para cada caminho de origem
   referenciado (raiz e escopo de item de cada coleção), registra:
   - destinos **diretos** (regra = referência pura ao campo) com facetas,
     obrigatoriedade e `nillable` — é o que define o que é entrada válida;
   - dicas de tipo pelos parâmetros tipados dos operadores
     (`RuleOperatorMetadata`: texto, número, data…) quando o campo só entra
     em expressões;
   - tabelas DE-PARA em que o campo é chave (`lookup`).
   Limites, enumerações, nulos e casos inválidos só são gerados para campos
   cujo **todos** os usos com restrição são diretos: é o único caso em que
   a restrição do destino vale, sem dúvida, para a entrada. Campos usados
   em expressões recebem valores típicos compatíveis com as dicas.
3. **Suíte estrutural determinística** (`StructuralSuiteBuilder`): um
   conjunto fixo e pequeno de casos — sem produto cartesiano:
   - `obrigatorios`: só campos que alimentam destinos obrigatórios (e a
     chave da coleção, com 1 item);
   - `completo`: todos os campos, valores típicos;
   - `limite-minimo` / `limite-maximo`: cada campo direto no seu limite
     (tamanho mín./máx., menor/maior valor, dígitos);
   - `enumeracao-k`: o k-ésimo valor de cada enumeração em paralelo (tantos
     casos quanto a maior enumeração, até 16) — todo valor coberto ao menos
     uma vez; patterns cobertos por valor gerado a partir da expressão
     regular (`PatternValueGenerator`, subconjunto usual de XSD; pattern
     fora do subconjunto vira item "não coberto" no relatório, nunca valor
     inventado);
   - `repeticao-vazia` (se a coleção de destino aceita zero) e
     `repeticao-varios` (3 itens, ou `maxOccurs` se menor);
   - `nulos`: um caso com todo campo direto que só alimenta destino
     `nillable` enviado como `null` — separado da ausência, que é exercitada
     pelo caso `obrigatorios` (campos que só alimentam opcionais ficam de
     fora) e, para obrigatórios não-nillable sem default, pelos inválidos
     `obrigatorio-ausente`/`obrigatorio-nulo`;
   - `choice`: enquanto o motor não mapeia `xs:choice`, cada choice do
     destino aparece no relatório como "não aplicável: conteúdo não
     mapeável", nunca como coberto;
   - **inválidos esperados**: para cada restrição crítica de campo direto
     (obrigatório ausente, tamanho, enumeração, pattern, limite, dígitos,
     tipo), um caso que a viola — esperado: geração reprovada com
     diagnóstico crítico. Um inválido que gera XML válido é falha.
   Os valores dos casos não saem da suíte: relatório e diagnósticos trazem
   nome do caso, categoria, código e XPath.

   Salvaguardas (revisão independente da Fase 8):
   - **Nunca "aprova por omissão"**: campo sem valor válido gerável fica fora
     dos casos; se alimenta destino obrigatório, a suíte ganha um caso
     reprovado `SYNTHETIC_VALUE_UNAVAILABLE` (erros nesse destino não
     mascaram os demais defeitos). Relatório com zero casos nunca é aprovado.
     Antes de desistir, o gerador usa o valor do campo num cenário funcional
     do autor (fica anotado como "não coberto" para limites e inválidos).
   - **Teto de geração** (`SyntheticValues.MaxTextLength` = 4096): tamanho
     ou pattern acima do teto vira "não coberto", sem alocar o valor inteiro.
     Faceta numérica acima de `int` satura (o leitor) e, quando o próprio
     compilador de schema do .NET estoura, vira erro de importação (400).
   - Campo direto que também é chave de DE-PARA usa uma chave da tabela;
     datas respeitam `min/maxInclusive`; limite exclusivo sem
     `fractionDigits` usa passo 0.0001.
4. **Cenários funcionais obrigatórios**: `FunctionalScenario` ganha
   `Assertions` (`Exists`, `Absent`, `Equals`, `Count` por caminho lógico,
   o mesmo do editor). A publicação exige ao menos um cenário com XML
   esperado **ou** asserções (`FUNCTIONAL_SCENARIO_REQUIRED`); cenário só
   com "deve gerar" continua aceito, mas não conta para o mínimo.
5. **Gate único para toda publicação pública**
   (`PublicationGatePipeline`: compilação → estrutural → funcional). Os
   três são **obrigatórios e fixos**: gates customizados só se somam
   (`additionalGates`), nunca os substituem; exceção dentro de um gate
   reprova (`GATE_EXECUTION_FAILED`). O
   caminho por camadas e o publicador direto do SDK passam pelo mesmo
   pipeline, com auditoria e publicação condicionada à versão ativa lida
   (fecha W1). `SnapshotPublisher` vira `internal` de `Application` — fora
   dela não existe publicação sem gate. O repositório (porta de
   infraestrutura) segue podendo gravar direto; ele não é superfície de
   produto.
6. **Regressão em atualização de camadas**: toda atualização (proposta ou
   automática) roda o pipeline completo sobre a resolução proposta e
   registra, por cenário funcional, se a saída mudou em relação à versão
   ativa (comparação semântica; só XPath da primeira diferença, sem valores)
   — `TemplateUpdate.Regressions`. Informativo: bloqueio vem dos testes.
7. **Relatório de qualidade** (`QualityReport`): casos por categoria,
   aprovados/reprovados, itens de cobertura não aplicáveis e por quê. Vai
   no resultado da publicação, na atualização e no endpoint de desenho
   `POST /v1/design/quality` (sem estado, como `/v1/design/test`), exibido
   no editor.

## Alternativas consideradas

- **Gerar dados a partir de um contrato de origem declarado**: não existe
  contrato de origem tipado persistido; exigir um agora seria mudar o
  editor e o formato de template da Fase 6. Rejeitada nesta fase; o
  perfilador é substituível quando o contrato existir.
- **Limites e inválidos também para campos usados em expressões**: a
  restrição do destino não vale para a entrada (ex.: `concat("C-", Numero)`
  numa `maxLength`) — geraria reprovações falsas. Rejeitada.
- **Produto cartesiano ou pairwise completo**: explosão combinatória
  proibida pela seção 19; o conjunto fixo cobre cada valor/limite ao menos
  uma vez. Rejeitada.
- **Manter publicação direta sem gate para compatibilidade**: contraria o
  gate da fase. Rejeitada; a assinatura do SDK muda (pacotes ainda não
  distribuídos fora do repositório).

8. **Decimais sem arredondamento silencioso**: o motor completa zeros até
   2 casas (ou até `fractionDigits`, se menor) e nunca descarta dígito
   significativo; casas a mais que o XSD permite reprovam na validação.
   Antes, o formato fixo `"0.00"` arredondava (1500.505 → "1500.51";
   0.0275 → "0.03"). Mudança de comportamento em execução para snapshots já
   publicados: entrada com casas a mais passa a reprovar em vez de sair
   arredondada — aceita, porque arredondar valor fiscal sem regra explícita
   é defeito; quem precisar arredondar declara a regra (`formatNumber`).

## Consequências

- Todo template publicado passou pelos mesmos testes, venha do SDK direto,
  do vínculo por camadas ou (Fase 9) da API/editor.
- O perfil inferido pode não conhecer restrições reais da origem: um valor
  típico pode reprovar uma expressão legítima. O relatório aponta caso e
  XPath; a correção é ajustar a regra ou (quando existir) declarar o
  contrato de origem. Risco aceito e registrado.
- Fixtures de teste precisam de cenário funcional com asserções/XML
  esperado para publicar.
- Cobertura de `choice` fica pendente até o motor mapear `choice` — lacuna
  da seção 19 conscientemente aberta (não há regra dentro de `choice` para
  exercitar hoje).
- Limites com pattern: o caso de limite máximo usa o maior valor do pattern
  (repetição aberta = mínimo + 2), não necessariamente o `maxLength`; datas
  não têm inválidos de mínimo/máximo. Ambos aparecem em "não coberto".
- Reprovação falsa conhecida: filtro ou condição sobre literal específico
  (`equals(Tipo, "S")`) com valor típico diferente pode deixar coleção ou
  destino obrigatório vazio. Correção: cenário funcional do autor com o
  valor (não há dispensa de caso; fica para quando existir contrato de
  origem declarado).
