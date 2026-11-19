# Captura diagnóstica

Logs normais nunca trazem dados de entrada nem XML. Para investigar uma
geração com dados reais, use a captura diagnóstica — temporária, opt-in,
mascarada, criptografada e auditada.

## Ligar

`POST /v1/organizations/{org}/templates/{t}/operations/{op}/diagnostics/capture`
com `{ "minutes": 30 }` (máximo 1440) — permissão `diagnostic.capture`.
Exige `Tradux:Diagnostics:EncryptionKey` configurada (senão `503`).

Enquanto ligada, cada geração daquele template grava entrada e XML com os
campos de nome sensível mascarados (CPF/CNPJ, e-mail, telefone, nome,
endereço, inscrição, e as abreviações do leiaute nacional como `xLgr`, `nro`,
`IM`) — `***` + 2 últimos caracteres. Até 500 registros por janela e por
instância.

## Ler e apagar

- `GET …/diagnostics/records` — leitura auditada (`diagnostic.read`).
- `DELETE …/diagnostics/records` — apaga antes do prazo (`diagnostic.delete`).
- `DELETE …/diagnostics/capture` — desliga.
- Registros somem sozinhos na retenção (padrão 24 h) pelo índice TTL.

## Cuidados

- Valores em campos com nome comum (ex.: `valorServico`) aparecem legíveis
  para quem tem `diagnostic.capture`. Conceda essa permissão com parcimônia.
- `correlationId` vai para logs: nunca use CNPJ ou número de nota como
  correlação.
