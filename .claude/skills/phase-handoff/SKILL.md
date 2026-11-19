---
name: phase-handoff
description: Use ao final de qualquer sessão de trabalho, ou quando o contexto estiver ficando curto, para persistir o estado do projeto de forma que outra sessão consiga continuar sem perder informação.
---

# Encerrar uma sessão com handoff completo

1. Atualize `docs/PROJECT_STATE.md` com, no mínimo: fase ativa, objetivo
   atual, o que foi concluído nesta sessão, o que está em andamento, o
   próximo passo exato (ação concreta, não vaga), decisões tomadas, dúvidas
   abertas, riscos/bloqueios, arquivos alterados, comandos de build/teste e o
   último resultado real de testes, e dívida técnica aceita conscientemente.
2. Reescreva `handoffs/CURRENT.md` do zero (não acumule) com: Estado, O que
   foi verificado, Próxima tarefa exata, Arquivos relevantes, Comandos para
   reproduzir, Riscos, Não fazer.
3. Se `handoffs/CURRENT.md` já existia com um handoff de uma tarefa que foi
   concluída nesta sessão, mova a versão antiga para
   `handoffs/archive/AAAA-MM-DD-descricao.md` antes de sobrescrever.
4. Qualquer decisão estável tomada nesta sessão (arquitetura, contrato,
   trade-off aceito permanentemente) precisa estar em `docs/DECISIONS.md` ou
   em um ADR — nunca apenas no histórico de chat.
5. Se algo no repositório ficou divergente da documentação (ex.: um teste
   quebrado que não foi corrigido, um TODO deixado), registre isso
   explicitamente em "riscos e bloqueios" — não deixe implícito.
6. Escreva o próximo passo como uma frase acionável que alguém sem memória
   desta sessão consiga executar (ex.: "implementar o operador `lookup` em
   `Tradux.Engine/Operators/`, seguindo o contrato de
   `rule-operator-design`"), nunca como "continuar o trabalho".
