# ADR-0006: Formato de template e resolução de camadas (base, produto, cliente)

- **Status**: aceito
- **Data**: 2026-09-28
- **Fase**: 1 — Descoberta técnica e arquitetura executável

## Contexto

O requisito de origem define uma cadeia de resolução template-base da
prefeitura → camada de produto → camada de cliente → snapshot publicado
(seção 16), com as regras: base e produto são protegidos; cliente armazena
somente diferenças e extensões; a publicação materializa um snapshot
completo e imutável; a resolução deve explicar a origem de cada regra; e
deve existir rollback. É preciso definir como cada camada é representada e
como a cadeia é resolvida de forma explicável.

## Decisão

- **Representação por camada**: cada camada (base, produto, cliente) é um
  documento `templateLayer` contendo uma lista de *overrides* endereçados
  por um **identificador lógico estável do nó de destino** (um caminho
  semântico derivado do XSD, não um índice posicional), cada override com:
  operação (`add`/`replace`/`remove` de mapeamento ou condição), o
  mapeamento/regra em si, e metadados (autor, timestamp, comentário). A
  camada **base** contém o conjunto completo inicial de mapeamentos — não é
  um diff de nada, é a origem. **Produto** e **cliente** são estritamente
  diffs (apenas overrides) sobre a camada anterior na cadeia.
- **Resolução com proveniência explícita**: ao compilar/publicar, o motor
  aplica os overrides em ordem — cliente tem precedência sobre produto, que
  tem precedência sobre base, por nó endereçado — produzindo uma árvore de
  mapeamentos resolvida completa. Cada nó resolvido carrega sua proveniência
  (`resolvedFrom: {layer: base|produto|cliente, layerVersion, authorRef}`)
  gravada dentro do próprio snapshot/manifesto (ADR-0003/0004), consultável
  a qualquer momento — não apenas registrada em log de publicação. Isso
  cumpre diretamente "a resolução deve explicar a origem de cada regra"
  (seção 16).
- **Publicação e rollback**: a publicação materializa essa árvore resolvida
  com proveniência em um `compiledSnapshot` imutável; a camada cliente nunca
  é publicada isoladamente, apenas o resultado materializado. Rollback
  significa apontar o ponteiro de versão ativa (ADR-0005) de volta a um
  snapshot já materializado, sem re-resolver camadas.

## Alternativas consideradas

- **Cliente e produto armazenam cópia completa do template (não apenas
  diffs)**: rejeitada por contrariar explicitamente "cliente armazena
  somente diferenças e extensões" (seção 16); também dificultaria auditar o
  que mudou e aumentaria o risco de divergência silenciosa quando a base é
  corrigida.
- **Merge genérico tipo JSON merge patch, sem endereçamento explícito por
  nó**: rejeitada — um merge genérico não distingue bem "remover regra" de
  "não definir campo" e não produz naturalmente proveniência por regra; o
  editor visual (seção 12, comparar/testar) e o requisito de "explicar
  origem de cada regra" exigem endereçamento explícito e estável por nó.
- **Recalcular a cadeia de camadas a cada geração, sem publicação/
  materialização**: rejeitada por contrariar diretamente "publicação
  materializa snapshot completo e imutável" (seção 16) e a meta de
  performance (ADR-0001/ADR-0005) — tornaria cada geração dependente de
  resolver e compilar três documentos em vez de reutilizar um snapshot já
  pronto.
- **Identificador de nó por posição/índice (ex.: XPath posicional)**:
  rejeitada em favor de um identificador lógico/semântico estável, porque um
  índice posicional quebra quando a base evolui de forma compatível (ex.:
  novo campo inserido no meio da sequência), fazendo overrides de
  produto/cliente perderem a referência; um caminho semântico é estável
  entre versões MINOR compatíveis (ADR-0003).

## Consequências

- O editor visual (Fase 6) precisa exibir, para cada regra resolvida, de
  qual camada ela veio — isso é um requisito estrutural do formato desde já,
  não um recurso adicionado depois.
- A atualização automática de produto (seção 16) pode ser implementada como:
  re-resolver a camada cliente sobre a nova versão de produto, detectar
  conflitos (overrides de cliente que hoje endereçam um nó removido ou com
  tipo alterado na nova base/produto), rodar os testes obrigatórios (Fase
  8), e publicar somente se tudo passar — o endereçamento estável por nó
  viabiliza diretamente essa detecção de conflito.
- Exige que todo nó de destino receba um identificador lógico estável desde
  a importação do XSD (Fases 2–3) — dependência explícita do trabalho do
  agente `domain-xsd`.
- Base e produto "protegidos" (seção 16) exigem uma regra de autorização
  (seção 20) que impeça a camada cliente de gravar overrides fora do seu
  próprio documento — é uma regra de acesso, não apenas de modelo de dados,
  e deve ser reforçada explicitamente na Fase 9.
- Mitigação de `docs/operations/THREAT_MODEL.md` (TM-20, vazamento de
  certificado/chave via pacote exportado): nenhum campo de um override
  (`templateLayer`) aceita um blob de arquivo livre sem schema associado —
  todo valor de override é tipado pelo catálogo de operadores (ADR-0001,
  seção 13 do requisito de origem) ou é uma referência a um nó do XSD de
  destino. Isso fecha, no nível de formato de dados, a lacuna que o threat
  model apontou entre si e este ADR.
