# Regras — Testes

Aplica-se a `tests/` e a qualquer gate de publicação.

- Nenhum template pode ser publicado com um teste obrigatório falhando —
  estrutural ou funcional. Isso é um gate, não uma recomendação.
- Cobertura estrutural mínima por contrato: apenas obrigatórios; obrigatórios
  + opcionais; limites e tamanhos; enumerações e patterns; repetições; cada
  ramo de `choice`; nulos permitidos (`nillable`) tratados separadamente de
  campos ausentes (`minOccurs=0`); ao menos um caso inválido esperado por
  restrição crítica.
- Cobertura funcional mínima por template: ao menos um cenário autoral com
  entrada real de negócio e XML esperado ou asserções estruturais.
- Geração de dados sintéticos usa cobertura determinística — não gere o
  produto cartesiano de todas as variações possíveis.
- Comparação de XML em teste é sempre semântica (ignora diferenças
  irrelevantes de prefixo de namespace e formatação/whitespace), nunca
  comparação de string bruta.
- Todo agente que implementa código roda os testes do projeto afetado antes
  de reportar a tarefa como concluída, e registra o resultado real (nunca
  presumido) em `docs/PROJECT_STATE.md`.
- Testes de arquitetura (`Tradux.ArchitectureTests`) protegem os
  limites de dependência descritos em `.claude/rules/dotnet.md` — trate uma
  falha ali como bloqueio, não como aviso.
