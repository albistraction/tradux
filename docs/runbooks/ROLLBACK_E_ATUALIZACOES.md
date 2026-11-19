# Rollback e atualizações de camadas

Publicação e rollback trocam a versão servida por todas as instâncias em
milissegundos (Change Stream; poll de 30 s como rede de segurança), sem
parada e sem resposta misturando versões — verificado sob carga em duas
instâncias (Fase 11, `docs/operations/PERFORMANCE.md`).

## Voltar para a versão anterior

`POST /v1/organizations/{org}/templates/{t}/operations/{op}/rollback` com `{}`
(anterior à ativa) ou `{ "version": N }` — permissão `template.publish`.

- Reativa um snapshot já publicado (imutável), sem re-resolver camadas.
- As instâncias trocam o cache pelo Change Stream em segundos.
- `409`: não há versão anterior, ou a versão ativa mudou durante o rollback
  (outra publicação chegou antes) — confira `GET …/versions` e repita.
- Propostas de atualização pendentes feitas sobre a versão que saiu do ar
  ficam `Superseded`.

## Explicar uma versão

`GET …/versions/{v}/explanation`: camadas exatas e, por caminho, de onde veio
cada regra (definida, substituída, removida). Use antes de decidir rollback.

## Atualização de camada (fornecedor lançou nova versão)

- Política `Automatic`: a nova versão é reaplicada, testada e publicada só
  se passar em todos os gates; senão fica registrada como `Failed` e a versão
  ativa não muda.
- Política `Manual`: gera proposta (`GET …/updates`) com diferenças por
  caminho, conflitos, relatório dos testes estruturais e **regressões** por
  cenário (saída mudou? onde?). Decida com
  `POST …/updates/{id}/approve` ou `/reject` (`template.publish`).
- `409` ao aprovar: a proposta foi feita sobre outra versão ativa ou já foi
  decidida — ela é marcada `Superseded`; aguarde/gera nova proposta.
- Proposta presa em `Applying` (processo caiu no meio da aprovação): a
  próxima publicação ou rollback segue normalmente; a proposta fica
  registrada assim para investigação.

## Versão nova que rebaixa camada

Nova versão do cliente escrita sobre um produto mais antigo que o ativo
nunca se aplica sozinha: vira proposta com aviso `LAYER_DOWNGRADE`.
