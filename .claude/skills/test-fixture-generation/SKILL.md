---
name: test-fixture-generation
description: Use ao gerar dados sintéticos de teste a partir de um contrato/XSD, para cobertura estrutural determinística sem explosão combinatória. Também cobre a criação do cenário funcional obrigatório de um template.
---

# Gerar fixtures de teste a partir de um contrato

1. A partir do modelo canônico do schema (ver skill `xsd-contract-analysis`),
   gere fixtures determinísticas cobrindo, no mínimo:
   - Apenas campos obrigatórios preenchidos.
   - Obrigatórios e opcionais preenchidos.
   - Valores nos limites de tamanho/faixa (mínimo, máximo, um acima/abaixo do
     limite quando o caso inválido for relevante).
   - Cada valor de enumeração e cada `pattern` relevante.
   - Repetições: zero, um e múltiplos itens em coleções.
   - Cada ramo de `choice` isoladamente.
   - Casos de nulo permitido (`nillable`) versus campo ausente
     (`minOccurs=0`) — gere fixtures separadas para os dois.
   - Ao menos um caso inválido esperado por restrição crítica (para confirmar
     que a validação rejeita corretamente).
2. Evite explosão combinatória: não cruze todas as variações entre si. Prefira
   uma fixture por dimensão de cobertura (pairwise ou cobertura determinística
   documentada), não o produto cartesiano completo.
3. Para o cenário funcional obrigatório (autoral), peça ou registre: uma
   entrada realista de negócio e o XML esperado (ou asserções estruturais
   equivalentes), representando um caso de uso real do template.
4. Salve fixtures versionadas junto ao módulo/contrato a que pertencem, com
   nome descritivo do que cada uma cobre.
5. Nenhuma fixture deve conter dados fiscais reais de terceiros — use dados
   sintéticos plausíveis.
