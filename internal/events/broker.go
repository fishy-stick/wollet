package events

import "sync"

type Event struct {
	Type     string
	DeviceID string
}

type Broker struct {
	mu          sync.Mutex
	subscribers map[chan Event]struct{}
}

func NewBroker() *Broker {
	return &Broker{subscribers: make(map[chan Event]struct{})}
}

func (b *Broker) Subscribe() (<-chan Event, func()) {
	channel := make(chan Event, 16)
	b.mu.Lock()
	b.subscribers[channel] = struct{}{}
	b.mu.Unlock()
	return channel, func() {
		b.mu.Lock()
		if _, ok := b.subscribers[channel]; ok {
			delete(b.subscribers, channel)
			close(channel)
		}
		b.mu.Unlock()
	}
}

func (b *Broker) Publish(event Event) {
	b.mu.Lock()
	defer b.mu.Unlock()
	for channel := range b.subscribers {
		select {
		case channel <- event:
		default:
		}
	}
}
