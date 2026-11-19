# Regras — Documentação e continuidade

Aplica-se a `docs/`, `handoffs/` e `.claude/`.

- `CLAUDE.md` fica curto: regras permanentes, comandos de build/teste,
  arquitetura essencial e referências. Conhecimento especializado vai em
  `.claude/skills/`, `.claude/rules/` e `docs/`.
- `docs/PROJECT_STATE.md` é a fonte de verdade operacional. Sempre que o
  estado real do repositório divergir dele, corrija o documento antes de
  continuar — nunca trabalhe sobre um estado documentado que já sabe estar
  errado.
- `docs/ROADMAP.md` define fases e seus gates de saída. Uma fase não é
  declarada concluída sem cumprir o gate por escrito.
- Toda decisão estável (arquitetura, contrato público, trade-off aceito
  permanentemente) vira ADR em `docs/architecture/adrs/` e/ou entrada em
  `docs/DECISIONS.md`. Decisão importante nunca fica só no chat.
- `handoffs/CURRENT.md` é reescrito (não acumulado) a cada encerramento de
  sessão relevante. O handoff anterior, se ainda tiver valor histórico, vai
  para `handoffs/archive/`.
- Qualquer material indexável pelo RAG (seção 24 do requisito de origem)
  precisa de metadados de fonte e versão — nunca adicione conteúdo a
  `docs/rag/` sem isso.
- Sempre que o requisito de origem
  (`docs/requirements/PROMPT_MESTRE_CLAUDE.md`) for ambíguo sobre um
  ponto que afeta a implementação, registre a interpretação escolhida como
  decisão em `docs/DECISIONS.md`, não apenas na cabeça de quem implementou.
