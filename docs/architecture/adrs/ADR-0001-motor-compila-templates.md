# ADR-0001: Motor compila templates para representação executável, não interpreta a árvore de regras

- **Status**: aceito
- **Data**: 2026-09-28
- **Fase**: 1 — Descoberta técnica e arquitetura executável

## Contexto

A meta de performance do motor (seção 25 do requisito de origem) é que a
geração fique o mais próxima possível de um DE-PARA escrito manualmente em
código, dimensionado para 100–200 gerações/minuto com margem de teste a
~2.000/minuto. Um snapshot de template publicado é imutável (seção 17) e é o
mesmo conjunto de regras reutilizado em milhares de execuções até a próxima
publicação.

Interpretar a árvore de regras (percorrer nós, resolver operadores por
despacho dinâmico, reavaliar tipos) a cada chamada de geração paga esse custo
de interpretação em toda requisição, mesmo quando o snapshot não mudou. Isso
não atinge a meta de "performance próxima de código escrito à mão".

## Decisão

O motor compila o snapshot de template publicado, uma única vez por versão,
para uma representação executável (delegates construídos via
`System.Linq.Expressions`, ou equivalente que produza código nativo em tempo
de execução), em vez de interpretar a árvore de regras a cada execução de
geração. O resultado compilado é o que fica em cache (ver ADR-0005) e é
reutilizado por todas as gerações daquela versão de snapshot.

## Alternativas consideradas

- **Interpretar a árvore de regras a cada execução (tree-walking
  interpreter)**: mais simples de implementar inicialmente, mas paga o custo
  de despacho/parsing por regra e por campo em toda geração; não se aproxima
  da meta de performance de código escrito à mão — rejeitada.
- **Reescrever o núcleo em C++ com interop via P/Invoke a partir do SDK/API em
  C#**: rejeitada porque o SDK é obrigatoriamente uma biblioteca C# (seção 1
  do requisito de origem), e cruzar a fronteira managed/native por campo
  mapeado tende a ser mais lento do que compilar para IL nativo dentro do
  próprio processo .NET, além de perder acesso direto a `System.Xml` e
  `XmlSchemaSet`, complicar o build multiplataforma e adicionar uma segunda
  toolchain — sem ganho justificado, já que a meta de vazão (seção 25) é
  modesta (100–200/min, margem a ~2.000/min).
- **VM de bytecode própria do projeto**: reinventaria o que Expression Trees
  e a JIT do .NET já resolvem, com maior custo de manutenção e sem evidência
  de necessidade — rejeitada.

## Consequências

- O "Compilador de Template" passa a ser um componente central e não trivial
  do motor (`Tradux.Engine`), com necessidade de testes e benchmarks
  dedicados (fases 4 e 11).
- O cache de snapshot compilado (seção 21, ver ADR-0005) fica ainda mais
  crítico: a primeira geração após uma publicação paga o custo de compilação;
  deve existir a opção de pré-aquecimento (warm-up) no boot ou logo após
  publicar.
- O compilador só pode traduzir o catálogo fechado de operadores (seção 13);
  nunca deve gerar código a partir de entrada arbitrária do usuário — isso
  reforça, em vez de enfraquecer, o limite "nenhum código arbitrário é
  executado" (seção 28).
- Diagnóstico fica mais difícil de depurar no nível de stack trace de
  Expression Trees compiladas; o motor precisa mapear exceções de volta para
  caminho/regra de origem (seção 18) explicitamente, não depender do stack
  trace bruto.
- Débito técnico aceito conscientemente: a compilação é um passo síncrono
  adicional no caminho de publicação/primeiro cache-miss; seu tempo deve ser
  medido e reportado separadamente do tempo de execução (seção 25 exige
  "benchmarks separados para compilação e execução").
