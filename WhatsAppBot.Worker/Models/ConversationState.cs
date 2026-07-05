namespace WhatsAppBot.Worker.Models;

public enum ConversationState
{
    Idle,
    AwaitingMenuSelection,
    AwaitingName,
    AwaitingServiceSelection,
    AwaitingBarberSelection,
    AwaitingDateSelection,
    AwaitingTimeSelection,
    ConfirmingAppointment,
    // Adicionado ao FINAL para preservar os valores inteiros já persistidos no banco.
    // Usado apenas no fluxo CarWash (lava-jato): pergunta o veículo (texto livre) após o serviço.
    AwaitingVehicle,
    // Fluxo de assinatura (Barbershop): seleção de plano + confirmação de PIX.
    AwaitingSubscription,
    // Fluxo de cancelamento com confirmação: seleção do agendamento + confirmação antes de deletar.
    AwaitingCancelConfirmation
}