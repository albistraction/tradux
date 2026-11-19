# Runbooks

Procedimentos operacionais da plataforma (Fase 10, ADR-0013 §13). Cada
runbook é indexado pelo RAG (`docs/rag/sources.json`) — mantenha os títulos
das seções descritivos, eles viram a âncora citada nas respostas.

| Runbook | Quando usar |
|---|---|
| [INSTALACAO_E_CONFIGURACAO.md](INSTALACAO_E_CONFIGURACAO.md) | Subir API/editor, configuração obrigatória, checklist de produção |
| [CREDENCIAIS_E_CHAVES.md](CREDENCIAIS_E_CHAVES.md) | Emitir, revogar e rotacionar credenciais e a chave da captura diagnóstica |
| [PROMOCAO_POR_PACOTE.md](PROMOCAO_POR_PACOTE.md) | Levar um template de um ambiente a outro, backup, execução offline |
| [ROLLBACK_E_ATUALIZACOES.md](ROLLBACK_E_ATUALIZACOES.md) | Voltar versão, decidir propostas de atualização de camadas |
| [CAPTURA_DIAGNOSTICA.md](CAPTURA_DIAGNOSTICA.md) | Investigar geração com dados reais, com segurança |
| [RAG_REINDEXACAO.md](RAG_REINDEXACAO.md) | Atualizar o índice de conhecimento e rodar as avaliações |
| [INCIDENTES.md](INCIDENTES.md) | Cache desatualizado, MongoDB indisponível, timeout de pattern, publicação recusada |
| [FALHAS_E_RECUPERACAO.md](FALHAS_E_RECUPERACAO.md) | Saúde, MongoDB fora, reinício/deploy, sobrecarga, limites de entrada, memória, desastre (Fase 11) |

Capacidade e resultados de carga: `docs/operations/PERFORMANCE.md`.
