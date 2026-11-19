---
name: architecture-adr
description: Use ao tomar ou revisar uma decisão arquitetural estável (versionamento, persistência, cache, formato de template, limites de módulo). Produz um ADR completo e o registra em docs/DECISIONS.md.
---

# Registrar uma decisão arquitetural (ADR)

1. Confirme que a decisão é estável (não muda de sessão para sessão) antes de
   criar o ADR — decisões provisórias ficam em `docs/PROJECT_STATE.md` na
   seção "dúvidas abertas", não em ADR.
2. Crie `docs/architecture/adrs/ADR-NNNN-titulo-curto.md` com o próximo número
   sequencial disponível (verifique os arquivos existentes na pasta).
3. Use esta estrutura mínima:
   - **Status**: proposto | aceito | substituído por ADR-XXXX.
   - **Contexto**: qual problema/força levou a esta decisão.
   - **Decisão**: o que foi decidido, em uma frase direta.
   - **Alternativas consideradas**: pelo menos uma, com motivo da rejeição.
   - **Consequências**: o que fica mais fácil e o que fica mais difícil.
4. Adicione uma linha em `docs/DECISIONS.md` apontando para o novo ADR, com
   data e fase em que foi tomada.
5. Se o ADR invalida ou substitui um ADR anterior, atualize o status do ADR
   antigo para "substituído por ADR-NNNN" — nunca apague um ADR antigo.
6. Nunca registre uma decisão importante apenas em chat ou em comentário de
   código — o ADR é a fonte de verdade.
