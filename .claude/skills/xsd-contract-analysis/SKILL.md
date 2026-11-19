---
name: xsd-contract-analysis
description: Use ao analisar um XSD ou pacote de XSDs novo (municipal, de produto ou de módulo composto) antes de modelá-lo na árvore interna tipada. Produz um relatório estrutural e uma lista de casos de borda.
---

# Analisar um contrato XSD antes de modelar

1. Identifique todos os arquivos do pacote e resolva `include`/`import`
   recursivamente. Liste namespaces declarados e seus prefixos.
2. Para cada elemento e atributo relevante, registre: tipo, `minOccurs`,
   `maxOccurs`, `nillable`, `default`, `fixed`, e se está dentro de
   `sequence`, `choice` ou `all`.
3. Trate "opcional" (`minOccurs=0`) e "aceita nulo" (`nillable=true`) como
   conceitos distintos — nunca colapse os dois em um único booleano.
4. Liste explicitamente:
   - Cada ramo de `choice` e o que cada ramo implica no XML final.
   - Tipos com restrições (`enumeration`, `pattern`, `minLength`/`maxLength`,
     `minInclusive`/`maxInclusive`).
   - Elementos recursivos ou com estrutura profundamente aninhada.
   - Qualquer referência a schema externo fora do pacote fornecido (risco de
     segurança — sinalize, não resolva silenciosamente).
5. Produza um documento em `docs/domain/` com: nome do módulo/versão, resumo
   estrutural, tabela de casos de borda, e fixtures mínimas necessárias para
   QA cobrir cada ramo de `choice` e cada restrição.
6. Se o XSD tiver ambiguidade que afete geração (ex.: ordem de elementos não
   clara, `choice` com overlap de tipos), registre como dúvida aberta em
   `docs/PROJECT_STATE.md` em vez de assumir uma interpretação silenciosamente.
