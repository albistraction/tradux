# ADR-0002: Versão .NET/ASP.NET Core LTS adotada

- **Status**: aceito (versão maior); patch/SDK exato a confirmar antes do
  primeiro `dotnet new` (ver "Consequências")
- **Data**: 2026-09-28
- **Fase**: 1 — Descoberta técnica e arquitetura executável

## Contexto

O requisito de origem (seção 7) exige "ASP.NET Core na versão LTS adotada
pelo projeto" sem fixar qual, deixando a decisão para um ADR desta fase
(registrado como dúvida aberta em `docs/PROJECT_STATE.md`).

O .NET segue um ciclo de suporte previsível: versões de número par recebem
suporte de longo prazo (LTS, ~3 anos) e versões ímpares recebem suporte
padrão (STS, ~18 meses), com um lançamento major por ano em novembro. Nessa
cadência, a sequência de LTS é .NET 6 (nov/2021) → .NET 8 (nov/2023) →
.NET 10 (nov/2025) → .NET 12 (nov/2027 esperado).

Hoje é 2026-09-28. Pela cadência acima, o .NET 10 seria a versão LTS mais
recente e vigente nesta data, e o .NET 8 estaria a cerca de dois meses do fim
do seu suporte (nov/2026). **Importante: o conhecimento deste agente tem
corte em janeiro de 2026, cerca de 9 meses antes da data de hoje.** Não há
como confirmar com certeza, a partir deste ambiente, se o .NET 10 realmente
foi lançado como planejado, se seu cronograma de suporte não mudou, ou qual é
a build/patch exata disponível em 2026-09-28. Esta decisão é tomada com base
na cadência histórica documentada publicamente pela Microsoft, não em
verificação direta da situação real nesta data.

## Decisão

Adotar o **.NET 10 (LTS)** como o Target Framework Moniker (`net10.0`) para
todos os projetos da solução (`src/` e `tests/`) e **ASP.NET Core 10** para
`Tradux.Api` e `Tradux.Web`, por ser — segundo a cadência de
lançamentos par/ímpar do .NET — a versão LTS vigente na data desta decisão.

Esta decisão sobre a versão **major** é tratada como aceita. A versão exata
de SDK/patch (ex.: `10.0.x`) e a confirmação de que o .NET 10 está
efetivamente disponível e é a LTS vigente **devem ser reverificadas no
repositório real, contra a página oficial de releases do .NET, antes do
primeiro `dotnet new` na Fase 2** — não a partir do conhecimento deste
agente. Se essa verificação contradisser a cadência aqui assumida, este ADR
deve ser revisto antes de iniciar a Fase 2.

## Alternativas consideradas

- **.NET 8 (LTS anterior)**: ainda suportado na data desta decisão, mas a
  cerca de dois meses do fim do seu ciclo de suporte (nov/2026) segundo a
  cadência padrão — rejeitada por iniciar um projeto novo e de vida longa em
  uma LTS prestes a expirar, criando pressão de migração logo no começo do
  projeto.
- **.NET 9 (STS)**: recebe apenas 18 meses de suporte e, pela cadência
  padrão, já estaria encerrado ou perto de encerrar na data desta decisão —
  inadequado para um produto fiscal de vida longa.
- **Acompanhar sempre a versão mais nova (STS incluído) para ter acesso a
  recursos recentes**: rejeitada — contraria a preferência do projeto por
  estabilidade dado o domínio fiscal, e obrigaria upgrades a cada ~18 meses
  sem necessidade.

## Consequências

- `global.json` deve fixar a versão exata do SDK assim que confirmada no
  ambiente real de desenvolvimento/CI (Fase 2), em vez de depender de "o que
  estiver instalado".
- `.claude/rules/dotnet.md` deve declarar net10.0 como TFM padrão do projeto
  e registrar a mesma ressalva de reverificação antes do primeiro código.
- Se a verificação na Fase 2 mostrar que o .NET 10 não é, de fato, a LTS
  disponível/vigente nesse momento (cronograma alterado, atraso de
  lançamento, etc.), este ADR precisa ser atualizado ou substituído antes de
  qualquer `dotnet new`; não presuma que a suposição aqui documentada é
  definitiva.
- Assumindo a cadência histórica, a próxima LTS esperada é o .NET 12
  (~nov/2027), dando uma janela de suporte de ~2 anos a partir do início real
  da Fase 2 antes de uma migração de LTS ser necessária.
- Débito técnico aceito conscientemente: esta ADR foi escrita sem acesso a
  verificação externa em tempo real; a confiança na versão major (.NET 10) é
  média-alta (baseada em cadência pública conhecida), e a confiança em
  qualquer detalhe de patch/build é baixa.
