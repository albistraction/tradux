# ADR-0005: Estratégia de cache de snapshot compilado e invalidação atômica

- **Status**: aceito
- **Data**: 2026-09-28
- **Fase**: 1 — Descoberta técnica e arquitetura executável

## Contexto

O requisito de origem exige que o snapshot compilado fique em cache com
"chave completa, invalidação atômica e proteção contra atualização parcial"
(seção 21), que o caminho normal de geração com cache aquecido não consulte
o MongoDB (seções 21 e 25), e que o runtime seja sem estado e escalável
horizontalmente (seção 7). O motor compila o snapshot para delegates
executáveis (ADR-0001), e cada nó de runtime pode ser uma instância de
processo independente.

## Decisão

- **Chave de cache**: `{organizationId}:{templateId}:{operation}`, resolvida
  para uma entrada imutável contendo `{activeVersion, contentHash, modelo
  compilado (ADR-0001)}`.
- **Escopo do cache — local ao processo, não distribuído**: cada instância
  de runtime mantém seu próprio cache em memória (ex.: `IMemoryCache` ou
  estrutura equivalente). Motivo: o artefato cacheado é um delegate
  compilado em tempo de execução (via `System.Linq.Expressions`), não é
  serializável de forma útil entre processos — cada nó precisaria recompilar
  de qualquer forma a partir de um payload intermediário. Como o runtime é
  sem estado e escala horizontalmente, cada instância pode sofrer cache-miss
  de forma independente e reconstruir a partir do MongoDB sem coordenação,
  ao custo apenas de recompilação local, nunca de inconsistência.
- **Invalidação atômica**: a publicação de uma nova versão grava, numa única
  transação multi-documento do MongoDB, o novo `compiledSnapshot` com
  `status = active` e rebaixa o anterior para `status = superseded` — nunca
  existem dois snapshots `active` simultâneos para a mesma chave lógica, ou
  seja, nunca é servido um estado parcial. Cada instância de runtime mantém
  uma assinatura em segundo plano (MongoDB Change Streams sobre a coleção de
  publicações/ponteiros de versão ativa) que, ao detectar mudança relevante,
  substitui atomicamente a referência da entrada de cache local inteira (não
  edita a entrada em uso — troca a referência, para que uma requisição em
  andamento nunca veja um objeto parcialmente atualizado). Um poll periódico
  de baixa frequência (ex.: a cada 30–60s) atua como rede de segurança caso
  o Change Stream fique indisponível, e uma carga completa ocorre no boot de
  cada instância. Nem o Change Stream nem o poll fazem parte do caminho de
  uma requisição de geração individual — a geração, com cache quente, lê
  apenas a entrada local.

## Alternativas consideradas

- **Cache distribuído (ex.: Redis) compartilhado entre instâncias**:
  rejeitada por ora — adicionaria um componente de infraestrutura extra sem
  evidência de necessidade (seção 25 veta introduzir infraestrutura sem
  evidência), e o artefato cacheado (delegate compilado em processo) não é
  diretamente compartilhável; cada nó teria que recompilar de qualquer
  forma. Pode ser revisitada na Fase 11 se a medição de tempo de compilação
  justificar.
- **Invalidação por consulta síncrona ao MongoDB a cada geração**: rejeitada
  por violar diretamente a meta "nenhuma leitura do MongoDB com cache
  aquecido" (seções 21 e 25).
- **Invalidação manual/operacional (reiniciar processos após publicar)**:
  rejeitada — incompatível com atualização automática (seção 16) e com a
  expectativa de que uma publicação se reflita sem downtime coordenado.
- **TTL curto isolado, sem mecanismo ativo de invalidação**: rejeitada como
  estratégia única — reintroduziria leitura periódica de alta frequência por
  instância mesmo sem mudança, e permitiria servir uma versão desatualizada
  até a expiração; aceitável apenas como rede de segurança combinada ao
  Change Stream, nunca como mecanismo primário.

## Consequências

- Requer que o MongoDB usado em produção seja um replica set (pré-requisito
  de Change Streams) — restrição de infraestrutura a documentar em
  `docs/operations/` antes da Fase 5.
- A primeira geração após o deploy de uma instância nova paga o custo de
  compilação (cache frio); mitigável com warm-up opcional no startup
  (ADR-0001).
- Cada instância tem sua própria cópia de cache; não há invalidação
  instantânea perfeita entre nós, mas é atômica por chave e por instância —
  a defasagem (staleness) fica limitada pela latência do Change Stream
  (tipicamente subsegundo) ou pelo poll de segurança.
- Débito técnico aceito conscientemente: a dependência de Change Streams
  precisa ser validada no ambiente real de implantação antes da Fase 5; se
  inviável, o poll periódico assume como mecanismo primário, com SLA de
  propagação maior e documentado explicitamente.
