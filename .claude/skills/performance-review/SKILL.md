---
name: performance-review
description: Use ao validar performance do caminho de geração de XML — cache aquecido, ausência de leitura ao MongoDB, benchmarks de compilação/execução e testes de carga.
---

# Checklist de revisão de performance

1. Confirme que, com cache de snapshot compilado aquecido, a geração de um
   XML não dispara nenhuma consulta ao MongoDB. Se disparar, é achado
   crítico — o caminho normal de execução não deve depender de banco.
2. Confirme que templates e XSDs relevantes estão pré-compilados antes da
   execução (não há parsing de XSD por requisição no caminho quente).
3. Rode (ou peça para rodar) benchmark separado para:
   - Compilação de um template/snapshot.
   - Execução de geração de XML a partir de um snapshot já compilado.
4. Valide a meta de vazão: 100–200 gerações/minuto como alvo declarado, com
   teste de margem em 10x o pico (até a ordem de 2000/minuto). Reporte
   percentis (p50/p95/p99), não apenas média.
5. Confirme runtime sem estado: duas instâncias concorrentes do processo
   produzem o mesmo resultado para a mesma entrada, sem estado compartilhado
   implícito em memória de processo único.
6. Verifique limites configuráveis de tempo, memória, profundidade de
   estrutura e tamanho de entrada — e que eles são realmente aplicados, não
   apenas documentados.
7. Não recomende fila ou microsserviço adicional sem um número concreto que
   comprove que o gargalo está fora do alcance de otimização no monólito
   modular atual.
8. Registre os resultados (números reais, não estimativas) no relatório da
   fase e em `docs/PROJECT_STATE.md`.
